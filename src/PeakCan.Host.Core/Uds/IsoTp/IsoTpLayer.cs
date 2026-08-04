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

    /// <summary>Raised when a complete message is reassembled.</summary>
    public event Action<byte[]>? MessageReceived;









}

