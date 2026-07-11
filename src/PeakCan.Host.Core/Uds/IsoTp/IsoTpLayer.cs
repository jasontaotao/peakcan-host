using System.Buffers;
using System.Collections.Concurrent;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace PeakCan.Host.Core.Uds.IsoTp;

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

    /// <summary>Create a new ISO-TP layer.</summary>
    /// <param name="config">CAN ID configuration for request/response.</param>
    /// <param name="sendFrame">Callback to send a CAN frame.</param>
    public IsoTpLayer(CanIdConfig config, Action<CanFrame> sendFrame)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(sendFrame);

        _config = config;
        _sendFrame = sendFrame;
    }

    /// <summary>
    /// v1.2.12 PATCH Item 2: async-callback ctor overload. The async callback
    /// is awaited internally so the SDK read thread never blocks on
    /// <c>.AsTask().Wait()</c> (the v1.2.11 deadlock root cause). Concurrent
    /// <see cref="SendMessageAsync"/> calls are serialized through a
    /// <see cref="SemaphoreSlim"/>(1,1) so the FF/CF sequence of one transport
    /// cannot interleave with another.
    /// </summary>
    /// <param name="config">CAN ID configuration for request/response.</param>
    /// <param name="sendFrame">Async callback to send a CAN frame.</param>
    /// <param name="logger">Optional logger for send-callback exceptions (logged at Error, not propagated).</param>
    public IsoTpLayer(CanIdConfig config, Func<CanFrame, Task> sendFrame, ILogger<IsoTpLayer>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(sendFrame);

        _config = config;
        _sendFrameAsync = sendFrame;
        _logger = logger;
    }

    /// <summary>
    /// Send a message, segmenting it into multiple CAN frames if needed.
    /// </summary>
    /// <param name="data">Message payload (up to 4095 bytes).</param>
    /// <exception cref="ArgumentException">Payload too long.</exception>
    public async Task SendMessageAsync(byte[] data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Length > MaxMessageLength)
            throw new ArgumentException($"Payload too long: {data.Length} > {MaxMessageLength}");

        if (data.Length <= MaxSingleFramePayload)
        {
            // Single Frame — route through the async send helper so the
            // async-ctor path actually delivers frames. The previous sync
            // SendCanFrame path silently dropped SF frames when only the
            // async callback was wired (v1.2.12 latent production bug M-6).
            await SendSingleFrameAsync(data).ConfigureAwait(false);
        }
        else
        {
            // Multi-frame: FF + CFs
            await SendMultiFrameAsync(data, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Process an incoming CAN frame. Call this from your CAN receive handler.
    /// </summary>
    /// <param name="frame">Received CAN frame.</param>
    public void ProcessFrame(CanFrame frame)
    {
        // Only process frames with our response CAN ID.
        if (frame.Id.Raw != _config.ResponseId)
            return;

        var isoFrame = IsoTpFrame.Decode(frame.Data.Span);

        switch (isoFrame.Type)
        {
            case IsoTpFrameType.Single:
                HandleSingleFrame(isoFrame);
                break;

            case IsoTpFrameType.First:
                HandleFirstFrame(isoFrame);
                break;

            case IsoTpFrameType.Consecutive:
                HandleConsecutiveFrame(isoFrame);
                break;

            case IsoTpFrameType.FlowControl:
                HandleFlowControl(isoFrame);
                break;
        }
    }

    /// <summary>Reset reassembly state (e.g., on timeout).</summary>
    public void Reset()
    {
        lock (_rxLock)
        {
            _rxInProgress = false;
            _rxBuffer = null;
        }
        CancelReceiveWatchdog();

        lock (_txLock)
        {
            _txWaitingForFc = false;
        }
    }

    /// <summary>
    /// v1.2.12 PATCH Item 2: dispose the send-path semaphore so the layer
    /// can be replaced cleanly. The app owns a single IsoTpLayer per
    /// channel, so this is called at process shutdown.
    /// </summary>
    public void Dispose()
    {
        _sendGate.Dispose();
    }

    private Task SendSingleFrameAsync(byte[] data)
    {
        var frame = new IsoTpFrame(IsoTpFrameType.Single, data: data);
        var canData = frame.Encode();
        return SendCanFrameAsync(canData, frameIndex: 0);
    }

    /// <summary>
    /// v1.2.13 PATCH Item 5: dispatch the encoded CAN frame through whichever
    /// callback the caller wired up. The async callback is awaited so an
    /// SDK hang is bounded by the layer's own timeouts (FC timeout, BS gate)
    /// instead of by <c>.AsTask().Wait()</c> on the SDK read thread.
    /// <para>
    /// On send-callback failure: log at Error (preserves the v1.2.12
    /// behaviour), increment <see cref="SendFailureCount"/>, and throw
    /// <see cref="IsoTpSendFailedException"/>. The throw propagates up
    /// through <see cref="SendMultiFrameAsync"/>, aborting the CF burst on
    /// the first failure (so a bus-off mid-FF no longer silently drops
    /// all subsequent CFs). The single-frame (TesterPresent) path passes
    /// <c>frameIndex: 0</c> and is allowed to throw; UdsClient / App
    /// factory catch sites handle it.
    /// </para>
    /// </summary>
    /// <param name="data">Encoded ISO-TP frame payload.</param>
    /// <param name="frameIndex">Position in the multi-frame burst (0 for FF/SF, 1..N for CF).</param>
    private async Task SendCanFrameAsync(byte[] data, int frameIndex)
    {
        var frame = new CanFrame(
            new CanId(_config.RequestId, FrameFormat.Standard),
            data,
            FrameFlags.None,
            default,
            default);

        if (_sendFrameAsync is not null)
        {
            try
            {
                await _sendFrameAsync(frame).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // v1.2.13 PATCH Item 5: propagate as IsoTpSendFailedException
                // so the caller (multi-frame transport, UDS layer, App
                // factory) can abort on the first failure instead of
                // silently dropping all subsequent CFs.
                if (_logger is not null)
                    LogIsoTpSendFailed(_logger, ex, frame.Id.Raw);
                Interlocked.Increment(ref SendFailureCount);
                throw new IsoTpSendFailedException(frame.Id.Raw, frameIndex, ex);
            }
            return;
        }

        // Legacy sync path: callers using the Action<CanFrame> ctor keep
        // their existing fire-and-forget semantics.
        _sendFrame?.Invoke(frame);
    }

    private async Task SendMultiFrameAsync(byte[] data, CancellationToken ct)
    {
        // v1.2.12 PATCH Item 2: serialize concurrent multi-frame sends so the
        // FF/CF sequence of one transport cannot interleave with another's.
        // We hold _sendGate for the whole multi-frame transport (FF → FC
        // wait → CFs → BS gate → ... → done). Single-frame sends use the
        // SendCanFrame async path but are not gated here (they are rare in
        // practice and ISO-TP §6.4 already covers arbitration semantics).
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_txLock)
            {
                _txWaitingForFc = true;
            }
            // v1.2.13 PATCH Item 5: reset the per-transport CF counter so
            // frameIndex starts at 1 for the first CF in this transport.
            // _sendGate serializes transports so this is safe (no other
            // transport can be in flight).
            _cfCounter = 0;

            // Send First Frame (frameIndex=0 by convention for FF).
            var ffData = data.AsMemory(0, Math.Min(6, data.Length));
            var ff = new IsoTpFrame(IsoTpFrameType.First, length: data.Length, data: ffData);
            await SendCanFrameAsync(ff.Encode(), frameIndex: 0).ConfigureAwait(false);

            // Wait for Flow Control
            if (!await WaitForFlowControlAsync(ct).ConfigureAwait(false))
                throw new TimeoutException("No Flow Control received");

            // Send Consecutive Frames. Honour the negotiated Block Size (BS):
            // when BS>0, pause after every BS-th CF and wait for the next FC.
            // BS=0 means "send all remaining CFs without further FC".
            int offset = 6;
            int sequence = 1;
            int cfInBlock = 0;
            int bs;
            lock (_txLock) { bs = _txBlockSize; }

            while (offset < data.Length)
            {
                ct.ThrowIfCancellationRequested();

                int chunkSize = Math.Min(7, data.Length - offset);
                var cfData = data.AsMemory(offset, chunkSize);
                var cf = new IsoTpFrame(IsoTpFrameType.Consecutive, sequenceOrStatus: sequence, data: cfData);
                // v1.2.13 PATCH Item 5: increment _cfCounter before each
                // CF send so frameIndex is 1-based. If the send throws,
                // the IsoTpSendFailedException aborts the burst and
                // _sendGate.Release runs in the finally.
                _cfCounter++;
                await SendCanFrameAsync(cf.Encode(), frameIndex: _cfCounter).ConfigureAwait(false);

                offset += chunkSize;
                sequence = (sequence + 1) & 0x0F;
                cfInBlock++;

                // Apply STmin delay (inter-CF pacing, ISO 15765-2 §6.5.5.4).
                // STmin units: 0x00..0x7F → ms, 0xF1..0xF9 → 100..900 µs.
                if (offset < data.Length)
                {
                    var st = StMinToTimeSpan(_txStMin);
                    if (st > TimeSpan.Zero)
                        await Task.Delay(st, ct).ConfigureAwait(false);
                }

                // Block-Size gate: after every BS CFs (when BS>0 and more remain),
                // wait for the next FC before continuing.
                if (bs > 0 && cfInBlock >= bs && offset < data.Length)
                {
                    lock (_txLock) { _txWaitingForFc = true; }
                    if (!await WaitForFlowControlAsync(ct).ConfigureAwait(false))
                        throw new TimeoutException("No Flow Control received (block-size gate)");
                    lock (_txLock) { bs = _txBlockSize; }
                    cfInBlock = 0;
                }
            }
        }
        finally
        {
            // v1.2.14 PATCH Task 1: close the _txWaitingForFc leak introduced
            // by v1.2.13 PATCH Item 5 throw path. Previously the inner catch
            // swallowed SendCanFrameAsync failures, so _txWaitingForFc was
            // eventually cleared by the next real FC arrival in
            // HandleFlowControl. Now that Item 5 propagates IsoTpSendFailed-
            // Exception out, the finally must own the reset. Must hold
            // _txLock because the flag is also written under that lock in
            // line 367/424 (initial true + BS-gate re-true) and Reset() (line 281).
            lock (_txLock)
            {
                _txWaitingForFc = false;
            }
            _sendGate.Release();
        }
    }

    /// <summary>
    /// Convert a raw STmin byte to a TimeSpan per ISO 15765-2 §6.5.5.4:
    /// <list type="bullet">
    /// <item>0x00..0x7F → 0..127 ms</item>
    /// <item>0x80..0xF0 → reserved, treated as 0 ms</item>
    /// <item>0xF1..0xF9 → 100..900 µs (100-µs resolution)</item>
    /// <item>0xFA..0xFF → reserved, treated as 0 ms</item>
    /// </list>
    /// </summary>
    private static TimeSpan StMinToTimeSpan(int stMinRaw)
    {
        if (stMinRaw <= 0x7F)
            return TimeSpan.FromMilliseconds(stMinRaw);
        if (stMinRaw >= 0xF1 && stMinRaw <= 0xF9)
        {
            // 100 µs = 1 tick (TimeSpan tick = 100 ns).
            return TimeSpan.FromTicks(stMinRaw - 0xF0);
        }
        return TimeSpan.Zero; // reserved range
    }

    private async Task<bool> WaitForFlowControlAsync(CancellationToken ct)
    {
        // Wait up to N_Bs for Flow Control. Default is ISO 15765-2's recommended
        // 1000 ms; overridable via FlowControlTimeout for slow ECUs.
        var timeout = _flowControlTimeout;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        while (!timeoutCts.Token.IsCancellationRequested)
        {
            lock (_txLock)
            {
                if (!_txWaitingForFc)
                    return true;
            }

            try
            {
                await Task.Delay(1, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Timed out waiting for FC. Caller will throw TimeoutException.
                return false;
            }
        }

        return false;
    }

    private void HandleSingleFrame(IsoTpFrame frame)
    {
        // Single frame: complete message
        MessageReceived?.Invoke(frame.Data.ToArray());
    }

    private void HandleFirstFrame(IsoTpFrame frame)
    {
        // v1.2.12 PATCH Item 8: refuse FFs declaring more than MaxMessageLength
        // bytes BEFORE allocating a 4 KB+ buffer. A malicious / fuzz ECU can
        // otherwise drive the host into OOM by streaming crafted FFs. The
        // Encode() method already caps the FF length field at 12 bits (4095),
        // so this check is defense-in-depth for any future encoder change or
        // for IsoTpFrame objects constructed directly via the public ctor.
        if (frame.Length > MaxMessageLength)
        {
            if (_logger is not null)
                LogIsoTpFfLengthTooLarge(_logger, frame.Length, MaxMessageLength);
            // Reset state so a subsequent valid FF can be reassembled.
            lock (_rxLock)
            {
                _rxInProgress = false;
                _rxBuffer = null;
                _rxExpectedLength = 0;
                _rxReceivedLength = 0;
                _rxExpectedSequence = 1;
            }
            CancelReceiveWatchdog();
            return; // drop, do not throw — keep the SDK read thread alive
        }

        lock (_rxLock)
        {
            _rxInProgress = true;
            _rxExpectedLength = frame.Length;
            _rxReceivedLength = 0;
            _rxExpectedSequence = 1;
            _rxBuffer = new byte[frame.Length];

            // Copy first chunk
            var firstChunk = frame.Data.Span;
            firstChunk.CopyTo(_rxBuffer.AsSpan(0, Math.Min(firstChunk.Length, frame.Length)));
            _rxReceivedLength += firstChunk.Length;

            // Send Flow Control
            SendFlowControl();

            // Start the N_Cr watchdog: if the next CF doesn't arrive in time,
            // abort reassembly so a fresh FF can be processed.
            StartReceiveWatchdog(expectedGeneration: 1);
        }
    }


    private void HandleConsecutiveFrame(IsoTpFrame frame)
    {
        // v1.2.12 PATCH Item 3: do all state mutation under _rxLock and
        // return the reassembled message (if any) to the caller. The
        // MessageReceived handler is then invoked OUTSIDE the lock, wrapped
        // in try/catch, so a buggy subscriber cannot corrupt ISO-TP
        // reassembly state nor propagate exceptions onto the SDK read
        // thread.
        byte[]? complete = HandleConsecutiveFrameLocked(frame);
        if (complete is null)
            return;

        try
        {
            MessageReceived?.Invoke(complete);
        }
        catch (Exception ex)
        {
            // Single source of truth for the "handler threw" event (id 3002).
            if (_logger is not null)
                LogIsoTpHandlerFailed(_logger, ex, complete.Length);
        }
    }

    /// <summary>
    /// v1.2.12 PATCH Item 3: lock-protected half of
    /// <see cref="HandleConsecutiveFrame"/>. Performs the sequence check
    /// and copies the new CF chunk into the reassembly buffer; if the
    /// message is complete, transfers ownership of the buffer to the
    /// caller (clearing <c>_rxBuffer</c> / <c>_rxInProgress</c>) and
    /// returns the assembled byte array. The lock is held for the
    /// duration of this method; the returned buffer is intended to be
    /// consumed AFTER the caller's lock scope ends.
    /// </summary>
    private byte[]? HandleConsecutiveFrameLocked(IsoTpFrame frame)
    {
        lock (_rxLock)
        {
            if (!_rxInProgress || _rxBuffer is null)
                return null;

            // Validate sequence number
            if (frame.SequenceOrStatus != _rxExpectedSequence)
            {
                // Sequence error: abort reassembly
                _rxInProgress = false;
                _rxBuffer = null;
                CancelReceiveWatchdog();
                return null;
            }

            // Copy data
            int remaining = _rxExpectedLength - _rxReceivedLength;
            int chunkSize = Math.Min(frame.Data.Length, remaining);
            frame.Data.Span.Slice(0, chunkSize).CopyTo(_rxBuffer.AsSpan(_rxReceivedLength, chunkSize));
            _rxReceivedLength += chunkSize;
            _rxExpectedSequence = (_rxExpectedSequence + 1) & 0x0F;

            // Check if complete
            if (_rxReceivedLength >= _rxExpectedLength)
            {
                var complete = _rxBuffer;
                _rxInProgress = false;
                _rxBuffer = null;
                CancelReceiveWatchdog();
                return complete;
            }

            // Re-arm the N_Cr watchdog for the next CF slot.
            StartReceiveWatchdog(expectedGeneration: _rxExpectedSequence);
            return null;
        }
    }


    private void SendCanFrame(byte[] data)
    {
        var frame = new CanFrame(
            new CanId(_config.RequestId, FrameFormat.Standard),
            data,
            FrameFlags.None,
            default,
            default);

        // Legacy sync path. In practice the async ctor is the new default,
        // so the old Action<CanFrame> callback is set when this method runs.
        _sendFrame?.Invoke(frame);
    }

}

/// <summary>
/// CAN ID configuration for ISO-TP communication.
/// </summary>
public sealed record CanIdConfig
{
    /// <summary>CAN ID for request frames (client → ECU).</summary>
    public uint RequestId { get; init; }

    /// <summary>CAN ID for response frames (ECU → client).</summary>
    public uint ResponseId { get; init; }

    /// <summary>
    /// Functional CAN ID for broadcast requests (optional).
    /// Used for functional addressing (e.g., 0x7DF for OBD-II).
    /// </summary>
    public uint? FunctionalId { get; init; }
    // === Flow F methods moved to IsoTpLayer/FlowControlFlow.cs (W9 Task 1) ===
    // === Flow G methods moved to IsoTpLayer/LoggingFlow.cs (W9 Task 2) ===
    // === Flow E methods + WatchdogHandle class + _rxWatchdog field moved to IsoTpLayer/WatchdogFlow.cs (W9 Task 3) ===
}
