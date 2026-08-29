using System.Buffers;
using System.Collections.Concurrent;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace PeakCan.HIL.Core.Uds.IsoTp;

/// <summary>
/// ISO 15765-2 (ISO-TP) transport layer. Handles segmentation and
/// reassembly of messages longer than 7 bytes (classic CAN) or
/// 63 bytes (CAN FD).
/// <para>
/// <b>Thread-safety:</b> This class is thread-safe. Frame reception
/// and transmission are handled via callbacks that are invoked on
/// the caller's thread.
/// </para>
/// </summary>
public sealed partial class IsoTpLayer : IDisposable
{
    /// <summary>Maximum payload size for Single Frame (classic CAN).</summary>
    public const int MaxSingleFramePayload = 7;

    /// <summary>Maximum payload size for a complete message (4095 bytes per ISO-TP).</summary>
    public const int MaxMessageLength = 4095;

    /// <summary>Default N_Bs: max time to wait for a Flow Control after sending a First Frame (ISO 15765-2 §6.7).</summary>
    public static readonly TimeSpan DefaultFlowControlTimeout = TimeSpan.FromMilliseconds(1000);

    /// <summary>Default N_Cr: max time to wait between Consecutive Frames before aborting reassembly (ISO 15765-2 §6.7).</summary>
    public static readonly TimeSpan DefaultReceiveTimeout = TimeSpan.FromMilliseconds(1000);

    private readonly CanIdConfig _config;
    private readonly Action<CanFrame>? _sendFrame;
    private readonly Func<CanFrame, Task>? _sendFrameAsync;
    private readonly ILogger<IsoTpLayer>? _logger;

    /// <summary>
    /// CAN ID used for transmitted frames. Defaults to <see cref="CanIdConfig.RequestId"/>.
    /// For ECU simulators, set to <see cref="CanIdConfig.ResponseId"/> so responses
    /// go out on the ID the HIL listens on.
    /// </summary>
    private readonly uint _txCanId;

    /// <summary>
    /// v1.2.13 PATCH Item 5: number of multi-frame send failures since
    /// process start. Incremented when SendCanFrameAsync's catch arm fires
    /// AND we throw out (not the swallow path which no longer exists for
    /// multi-frame sends). Used by tests to assert regression behaviour.
    /// </summary>
    internal int SendFailureCount;

    /// <summary>
    /// v1.2.14 PATCH Task 1: read-only accessor for the internal
    /// <see cref="_txWaitingForFc"/> flag, exposed via <c>InternalsVisibleTo</c>
    /// to test assemblies. Used by tests to assert that the flag is
    /// cleared after SendMultiFrameAsync throws (closes the leak
    /// introduced by v1.2.13 PATCH Item 5 throw path).
    /// </summary>
    internal bool TxWaitingForFcForTesting
    {
        get
        {
            lock (_txLock) { return _txWaitingForFc; }
        }
    }

    /// <summary>
    /// v1.2.13 PATCH Item 5: transient counter of consecutive frames sent
    /// in the current multi-frame transport. Reset at the start of every
    /// transport (SendMultiFrameAsync). The _sendGate serializes transports
    /// so this field is not shared across concurrent ones.
    /// </summary>
    private int _cfCounter;
    // v1.2.12 PATCH Item 2: serialize FF/CF emission across concurrent
    // SendMultiFrameAsync callers so transports cannot interleave (per
    // ISO 15765-2 §6.5). Replaces the implicit assumption that the SDK
    // send is synchronous and ordered.
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    // Reassembly state for incoming messages.
    private readonly object _rxLock = new();
    private byte[]? _rxBuffer;
    private int _rxExpectedLength;
    private int _rxReceivedLength;
    private int _rxExpectedSequence;
    private bool _rxInProgress;


    /// <summary>
    /// v1.2.13 PATCH Item 1: test-visible count of how many times
    /// CancelReceiveWatchdog deferred a Dispose to the threadpool.
    /// Used by tests to assert that the deferred-Dispose path is taken
    /// (and not a synchronous one).
    /// </summary>
    internal long _watchdogDisposalDeferredCount;


    // Transmission state for outgoing messages.
    private readonly object _txLock = new();
    private int _txBlockSize;
    private int _txStMin;
    private bool _txWaitingForFc;

    /// <summary>
    /// True once a Flow Control frame has been accepted by <see cref="FlowControlFlow.HandleFlowControl"/>.
    /// Lets UI readers distinguish the initial all-zero state (no FC seen yet) from a
    /// genuine BS=0/STmin=0 Flow Control.
    /// </summary>
    private bool _hasReceivedFc;

    private TimeSpan _flowControlTimeout = DefaultFlowControlTimeout;
    private TimeSpan _receiveTimeout = DefaultReceiveTimeout;

    /// <summary>
    /// Maximum time to wait for a Flow Control frame after sending a First Frame
    /// (ISO 15765-2 N_Bs). Default: 1000 ms. Configurable for slow ECUs.
    /// </summary>
    public TimeSpan FlowControlTimeout
    {
        get => _flowControlTimeout;
        set => _flowControlTimeout = value;
    }

    /// <summary>
    /// Maximum time to wait between Consecutive Frames during reassembly
    /// (ISO 15765-2 N_Cr). When this expires, _rxInProgress is reset so a
    /// subsequent First Frame can be reassembled. Default: 1000 ms.
    /// </summary>
    public TimeSpan ReceiveTimeout
    {
        get => _receiveTimeout;
        set => _receiveTimeout = value;
    }

    /// <summary>
    /// The CAN ID configuration this layer was constructed with (request/response IDs,
    /// functional ID, frame format). <see cref="CanIdConfig"/> is an immutable record,
    /// so the reference is safe to expose read-only.
    /// </summary>
    public CanIdConfig Config => _config;

    /// <summary>
    /// Atomic snapshot of the transmit-side flow-control state (single lock acquisition)
    /// for read-only display, e.g. the host app's communication-parameters panel.
    /// <see cref="IsoTpTxSnapshot.HasReceivedFlowControl"/> is false until the first
    /// Flow Control frame is accepted, in which case StMinRaw/BlockSize are the
    /// initial zeros rather than ECU-provided values.
    /// </summary>
    public IsoTpTxSnapshot SnapshotTxState()
    {
        lock (_txLock)
        {
            return new IsoTpTxSnapshot(
                _txStMin,
                StMinToTimeSpan(_txStMin),
                _txBlockSize,
                _txWaitingForFc,
                _hasReceivedFc);
        }
    }

    /// <summary>Raised when a complete message is reassembled.</summary>
    public event Action<byte[]>? MessageReceived;
}

/// <summary>
/// Read-only point-in-time view of <see cref="IsoTpLayer"/>'s transmit-side
/// flow-control state, taken atomically by <see cref="IsoTpLayer.SnapshotTxState"/>.
/// </summary>
/// <param name="StMinRaw">
/// Raw STmin byte from the last accepted Flow Control (ISO 15765-2 §6.5.5.4 encoding:
/// 0x00-0x7F ms, 0xF1-0xF9 100-µs units, other values reserved).
/// </param>
/// <param name="StMinDelay">Effective STmin delay decoded from <paramref name="StMinRaw"/>.</param>
/// <param name="BlockSize">
/// Block size (BS) from the last accepted Flow Control; 0 means unlimited consecutive
/// frames between Flow Controls.
/// </param>
/// <param name="WaitingForFlowControl">
/// True while a multi-frame send is parked waiting for the ECU's Flow Control (N_Bs window).
/// </param>
/// <param name="HasReceivedFlowControl">
/// False until the first Flow Control frame is accepted; when false, StMinRaw/BlockSize
/// are initial zeros, not ECU-provided values.
/// </param>
public readonly record struct IsoTpTxSnapshot(
    int StMinRaw,
    TimeSpan StMinDelay,
    int BlockSize,
    bool WaitingForFlowControl,
    bool HasReceivedFlowControl);
