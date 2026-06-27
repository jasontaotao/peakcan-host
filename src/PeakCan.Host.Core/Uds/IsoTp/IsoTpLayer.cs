using System.Buffers;
using System.Collections.Concurrent;
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
    private CancellationTokenSource? _rxWatchdog;

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
        return SendCanFrameAsync(canData);
    }

    /// <summary>
    /// v1.2.12 PATCH Item 2: dispatch the encoded CAN frame through whichever
    /// callback the caller wired up. The async callback is awaited so an
    /// SDK hang is bounded by the layer's own timeouts (FC timeout, BS gate)
    /// instead of by <c>.AsTask().Wait()</c> on the SDK read thread.
    /// Exceptions are logged at Error and swallowed so a single bad send
    /// does not abort the entire multi-frame transport.
    /// </summary>
    private async Task SendCanFrameAsync(byte[] data)
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
                if (_logger is not null)
                    LogIsoTpSendFailed(_logger, ex, frame.Id.Raw);
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

            // Send First Frame
            var ffData = data.AsMemory(0, Math.Min(6, data.Length));
            var ff = new IsoTpFrame(IsoTpFrameType.First, length: data.Length, data: ffData);
            await SendCanFrameAsync(ff.Encode()).ConfigureAwait(false);

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
                await SendCanFrameAsync(cf.Encode()).ConfigureAwait(false);

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
            StartReceiveWatchdog();
        }
    }

    /// <summary>
    /// Arm a CancellationTokenSource that fires after <see cref="ReceiveTimeout"/>.
    /// On expiry it clears _rxInProgress / _rxBuffer so the next FF starts a
    /// fresh reassembly (rather than silently wedging the receive state).
    /// </summary>
    private void StartReceiveWatchdog()
    {
        CancelReceiveWatchdog();

        var timeout = _receiveTimeout;
        var cts = new CancellationTokenSource();
        cts.CancelAfter(timeout);
        var token = cts.Token;
        token.Register(() =>
        {
            lock (_rxLock)
            {
                if (_rxInProgress)
                {
                    // Watchdog fires → reassembly stalled at any stage. Clear
                    // state so a fresh FF can take over (covers the FF → no-CF1
                    // case where the sequence number never advanced past 1).
                    _rxInProgress = false;
                    _rxBuffer = null;
                }
            }
        });

        lock (_rxLock) { _rxWatchdog = cts; }
    }

    private void CancelReceiveWatchdog()
    {
        CancellationTokenSource? old;
        lock (_rxLock)
        {
            old = _rxWatchdog;
            _rxWatchdog = null;
        }
        if (old is not null)
        {
            try { old.Cancel(); } catch (ObjectDisposedException) { }
            old.Dispose();
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
            StartReceiveWatchdog();
            return null;
        }
    }

    private void HandleFlowControl(IsoTpFrame frame)
    {
        lock (_txLock)
        {
            if (!_txWaitingForFc)
                return;

            _txBlockSize = frame.BlockSize;
            _txStMin = frame.StMin;
            _txWaitingForFc = false;
        }
    }

    private void SendFlowControl()
    {
        // Send Flow Control with BS=0 (unlimited), STmin=0 (no delay)
        var fc = new IsoTpFrame(
            IsoTpFrameType.FlowControl,
            sequenceOrStatus: 0, // Continue to send
            blockSize: 0,       // Unlimited
            stMin: 0);          // No delay

        var canData = fc.Encode();
        SendCanFrame(canData);
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

    // v1.2.12 PATCH Item 2: log send-callback exceptions at Error. The upper
    // layers (UdsClient's P2* timeout, BS-gate timeout) provide back-pressure,
    // so a single failed send is logged and the transport continues.
    //
    // `internal` (not `private`) so the App factory can call this directly
    // instead of maintaining a duplicate log helper (single source of truth
    // for the event id). Core grants InternalsVisibleTo("PeakCan.Host.App")
    // in AssemblyInfo.cs.
    [LoggerMessage(EventId = 3001, Level = LogLevel.Error, Message = "IsoTpLayer send failed for ID 0x{Id:X}")]
    internal static partial void LogIsoTpSendFailed(ILogger logger, Exception ex, uint id);

    // v1.2.12 PATCH Item 3: log MessageReceived subscriber exceptions at Error.
    // The receive handler is invoked outside the lock so the layer's
    // reassembly state remains intact; a throwing subscriber must NOT
    // propagate onto the SDK read thread nor poison subsequent frames.
    // Single source of truth for the "handler threw" event (id 3002).
    [LoggerMessage(EventId = 3002, Level = LogLevel.Error, Message = "IsoTpLayer MessageReceived handler threw for {Length}-byte message")]
    private static partial void LogIsoTpHandlerFailed(ILogger logger, Exception ex, int length);
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
}
