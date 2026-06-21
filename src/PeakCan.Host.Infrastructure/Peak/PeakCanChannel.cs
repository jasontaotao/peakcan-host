using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Peak.Can.Basic.BackwardCompatibility;
using PeakCan.Host.Core;
using PeakCan.Host.Infrastructure.Channel;

namespace PeakCan.Host.Infrastructure.Peak;

/// <summary>
/// PEAK PCAN-Basic adapter implementing <see cref="ICanChannel"/>. Wraps the
/// static <c>Peak.Can.Basic.BackwardCompatibility.PCANBasic</c> API for one
/// <see cref="ushort"/> channel handle.
/// <para>
/// Read path: a single background <see cref="Task"/> polls
/// <c>PCANBasic.Read</c> / <c>PCANBasic.ReadFD</c> until cancelled, raising
/// <see cref="FrameReceived"/> on the SDK thread.
/// </para>
/// <para>
/// Write path: <see cref="WriteAsync"/> formats the <see cref="CanFrame"/>
/// into a <c>TPCANMsg</c> / <c>TPCANMsgFD</c> and calls the synchronous
/// <c>PCANBasic.Write*</c>. The <c>TPCANMessageType</c> bit pattern selects
/// standard-vs-extended and FD-vs-classical; raw 11/29-bit IDs go into
/// <c>ID</c> without any IDE-bit flag.
/// </para>
/// <para>
/// Errors are translated via <see cref="PeakErrorMapper"/>; unexpected
/// exceptions are caught at the boundary and reported as
/// <see cref="ErrorCode.IoError"/> / <see cref="ErrorCode.HardwareNotAvailable"/>
/// rather than propagated, so the WPF ViewModel layer can render a message
/// without try/catch boilerplate.
/// </para>
/// <para>
/// <b>Read loop fault handling:</b> any exception thrown from the SDK read
/// calls is caught, logged at error level, and the loop backs off
/// (1ms / 10ms / 50ms per consecutive failure) to prevent a hot loop.
/// If <see cref="MaxConsecutiveReadFailures"/> consecutive failures
/// accumulate, the read loop gives up rather than busy-spinning on a
/// dead bus; the channel remains connected from the SDK's perspective
/// but no frames will be delivered. The classic and FD read blocks each
/// have their own try/catch so a subscriber that throws on
/// <see cref="FrameReceived"/> for a classic frame cannot skip the FD
/// read in the same iteration.
/// </para>
/// <para>
/// <b>Classic baud dispatch:</b> <see cref="BaudRate"/> in Core
/// no longer carries the PEAK <c>TPCANBaudrate?</c> field (Core must not
/// depend on the PEAK SDK per NetArchTest rule 2). For classic CAN
/// (<c>fd: false</c>) this adapter maps the four preset
/// <see cref="BaudRate.Name"/> values back to the matching
/// <c>PCAN_BAUD_*</c> enum via <see cref="ResolveClassicCode"/>.
/// </para>
/// </summary>
public sealed partial class PeakCanChannel : ICanChannel
{
    // Backoff schedule after consecutive read-loop failures. Resets to 0
    // whenever a read returns a non-error status (success or "queue empty").
    private static readonly int[] ReadLoopBackoffMs = { 1, 10, 50 };

    /// <summary>
    /// After this many consecutive read-loop failures, the loop gives up
    /// rather than busy-spinning on a dead bus / unloaded driver. The
    /// channel stays in the connected state from the SDK's perspective
    /// (so a future manual disconnect still works), but no frames will
    /// be delivered until the user calls Disconnect + Connect again.
    /// </summary>
    internal const int MaxConsecutiveReadFailures = 100;

    private readonly ushort _handle;
    private readonly ChannelConnectGate _gate = new();
    private readonly ILogger<PeakCanChannel> _logger;

    public ChannelId Id { get; }
    public bool IsConnected => _gate.IsConnected;
    public event Action<CanFrame>? FrameReceived;

    public PeakCanChannel(ChannelId id, ILogger<PeakCanChannel>? logger = null)
    {
        Id = id;
        _handle = id.Handle;
        // NullLogger keeps test paths that new up the channel directly
        // (no DI) free of logger plumbing while still letting production
        // capture read-loop failures via the registered ILogger.
        _logger = (ILogger<PeakCanChannel>?)logger ?? NullLogger<PeakCanChannel>.Instance;
    }

    public async Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default)
    {
        // Atomically: check not already connected, reserve a CTS. If the
        // token is already cancelled, TryEnter throws and the gate stays
        // clean.
        var enter = _gate.TryEnter(ct);
        if (!enter.IsSuccess) return enter;

        TPCANStatus status;
        try
        {
            if (fd)
            {
                status = PCANBasic.InitializeFD(_handle, baud.Descriptor);
            }
            else if (ResolveClassicCode(baud) is { } classic)
            {
                status = PCANBasic.Initialize(_handle, classic);
            }
            else
            {
                _gate.MarkFailed();
                return Result<Unit>.Fail(
                    ErrorCode.HardwareParameter,
                    $"BaudRate '{baud.Name}' has no classic CAN preset (use Can125kbps/Can250kbps/Can500kbps/Can1Mbps).");
            }
            if (!PeakErrorMapper.IsOk((uint)status))
            {
                _gate.MarkFailed();
                return MakeError(status);
            }
        }
        catch (Exception ex)
        {
            _gate.MarkFailed();
            return Result<Unit>.Fail(ErrorCode.HardwareNotAvailable, ex.Message);
        }

        // Read loop launched via Task.Run so the synchronous Init does not
        // block the caller; the cancellation token is the one reserved by
        // the gate, not the caller's ct (which may have been cancelled).
        var token = _gate.CurrentToken;
        var loop = Task.Run(() => ReadLoopAsync(token), ct);
        _gate.SetReadLoop(loop);
        return await Task.FromResult(Result<Unit>.Ok(default)).ConfigureAwait(false);
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        var (token, loop) = _gate.CaptureForDisconnect();
        if (loop is null) return;
        try { await loop.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected */ }
        // Best-effort teardown: the channel is going away regardless. If
        // the driver is already unloaded or in a bad state, we still mark
        // the channel disconnected.
        try { PCANBasic.Uninitialize(_handle); }
        catch { /* best-effort teardown */ }
        _gate.Dispose();
    }

    public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
    {
        if (!IsConnected)
        {
            return ValueTask.FromResult(Result<Unit>.Fail(ErrorCode.InvalidState, "Not connected"));
        }
        try
        {
            TPCANStatus status;
            if (frame.IsFd)
            {
                var msgType = TPCANMessageType.PCAN_MESSAGE_FD
                              | (frame.Id.IsExtended ? TPCANMessageType.PCAN_MESSAGE_EXTENDED : TPCANMessageType.PCAN_MESSAGE_STANDARD);
                if ((frame.Flags & FrameFlags.BitRateSwitch) != 0) msgType |= TPCANMessageType.PCAN_MESSAGE_BRS;
                if ((frame.Flags & FrameFlags.ErrorStateIndicator) != 0) msgType |= TPCANMessageType.PCAN_MESSAGE_ESI;
                var m = new TPCANMsgFD
                {
                    ID = frame.Id.Raw,
                    MSGTYPE = msgType,
                    DLC = (byte)Math.Min(frame.Dlc, (byte)15),
                    DATA = PeakCanFrameFormatter.ToFixedBytes64(frame.Data),
                };
                status = PCANBasic.WriteFD(_handle, ref m);
            }
            else
            {
                var msgType = frame.Id.IsExtended
                    ? TPCANMessageType.PCAN_MESSAGE_EXTENDED
                    : TPCANMessageType.PCAN_MESSAGE_STANDARD;
                var m = new TPCANMsg
                {
                    ID = frame.Id.Raw,
                    MSGTYPE = msgType,
                    LEN = (byte)Math.Min(frame.Dlc, (byte)8),
                    DATA = PeakCanFrameFormatter.ToFixedBytes8(frame.Data),
                };
                status = PCANBasic.Write(_handle, ref m);
            }
            return PeakErrorMapper.IsOk((uint)status)
                ? ValueTask.FromResult(Result<Unit>.Ok(default))
                : ValueTask.FromResult(MakeError(status));
        }
        catch (Exception ex)
        {
            return ValueTask.FromResult(Result<Unit>.Fail(ErrorCode.IoError, ex.Message));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        // DisposeAsync is now safe to call twice — the gate's CaptureFor-
        // Disconnect returns a null loop on the second call.
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        int consecutiveIterationsWithFailure = 0;
        while (!ct.IsCancellationRequested)
        {
            // Classic and FD reads each get their own try/catch. Previously
            // they shared one try, so an exception thrown from a FrameReceived
            // subscriber for a classic frame (e.g. a buggy decoder) would
            // skip the FD read in the same iteration, silently dropping FD
            // traffic until the next loop turn. This matches the per-sink
            // isolation pattern in ChannelRouter.
            bool gotAnyFrame = false;
            bool iterationFailed = false;
            try
            {
                while (PCANBasic.Read(_handle, out var msg, out var ts) == TPCANStatus.PCAN_ERROR_OK)
                {
                    EmitClassic(msg, ts);
                    gotAnyFrame = true;
                }
            }
            catch (Exception ex)
            {
                LogReadLoopException(_logger, Id.Handle, "classic", ex);
                iterationFailed = true;
            }
            try
            {
                while (PCANBasic.ReadFD(_handle, out var fdMsg, out var tsMicroseconds) == TPCANStatus.PCAN_ERROR_OK)
                {
                    EmitFd(fdMsg, tsMicroseconds);
                    gotAnyFrame = true;
                }
            }
            catch (Exception ex)
            {
                LogReadLoopException(_logger, Id.Handle, "FD", ex);
                iterationFailed = true;
            }
            // Count per-iteration, not per-throw, so a worst-case iteration
            // with both classic and FD failures still counts as 1 (matching
            // the pre-split semantics). Reset on any successful frame.
            if (iterationFailed && !gotAnyFrame) consecutiveIterationsWithFailure++;
            if (gotAnyFrame) consecutiveIterationsWithFailure = 0;

            if (consecutiveIterationsWithFailure >= MaxConsecutiveReadFailures)
            {
                // Don't busy-spin on a dead bus. Surface a single fatal
                // log and exit the loop; the channel stays "connected"
                // from the SDK's perspective so a manual disconnect
                // (and a fresh Connect) can recover.
                LogReadLoopGivingUp(_logger, Id.Handle, consecutiveIterationsWithFailure);
                return;
            }

            var delay = consecutiveIterationsWithFailure == 0
                ? 1
                : ReadLoopBackoffMs[Math.Min(consecutiveIterationsWithFailure - 1, ReadLoopBackoffMs.Length - 1)];
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Read loop threw on handle 0x{Handle:X2} ({Kind} read)")]
    private static partial void LogReadLoopException(ILogger logger, ushort handle, string kind, Exception error);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Read loop giving up on handle 0x{Handle:X2} after {Failures} consecutive failures — bus appears dead, call Disconnect+Connect to recover")]
    private static partial void LogReadLoopGivingUp(ILogger logger, ushort handle, int failures);

    private void EmitClassic(TPCANMsg m, TPCANTimestamp ts)
    {
        var isExtended = (m.MSGTYPE & TPCANMessageType.PCAN_MESSAGE_EXTENDED) != 0;
        var canId = new CanId(m.ID, isExtended ? FrameFormat.Extended : FrameFormat.Standard);
        var len = (byte)Math.Min((byte)m.LEN, (byte)8);
        var bytes = new byte[len];
        Array.Copy(m.DATA, bytes, len);
        var flags = FrameFlags.None;
        if ((m.MSGTYPE & TPCANMessageType.PCAN_MESSAGE_ERRFRAME) != 0) flags |= FrameFlags.ErrFrame;
        if ((m.MSGTYPE & TPCANMessageType.PCAN_MESSAGE_RTR) != 0) flags |= FrameFlags.Rtr;
        // TPCANTimestamp: millis (uint) + micros (ushort within ms).
        var frame = new CanFrame(canId, bytes, flags, Id, Timestamp.FromMillis(ts.millis, ts.micros));
        FrameReceived?.Invoke(frame);
    }

    private void EmitFd(TPCANMsgFD m, ulong tsMicroseconds)
    {
        var isExtended = (m.MSGTYPE & TPCANMessageType.PCAN_MESSAGE_EXTENDED) != 0;
        var canId = new CanId(m.ID, isExtended ? FrameFormat.Extended : FrameFormat.Standard);
        var flags = FrameFlags.Fd;
        if ((m.MSGTYPE & TPCANMessageType.PCAN_MESSAGE_BRS) != 0) flags |= FrameFlags.BitRateSwitch;
        if ((m.MSGTYPE & TPCANMessageType.PCAN_MESSAGE_ESI) != 0) flags |= FrameFlags.ErrorStateIndicator;
        if ((m.MSGTYPE & TPCANMessageType.PCAN_MESSAGE_ERRFRAME) != 0) flags |= FrameFlags.ErrFrame;
        var dlc = PeakCanFrameFormatter.DlcToBytes(m.DLC);
        var bytes = new byte[dlc];
        Array.Copy(m.DATA, bytes, dlc);
        // TPCANTimestampFD in this SDK version is a plain UInt64 microsecond count.
        var frame = new CanFrame(canId, bytes, flags, Id, Timestamp.FromMicroseconds(tsMicroseconds));
        FrameReceived?.Invoke(frame);
    }

    private static Result<Unit> MakeError(TPCANStatus s)
    {
        var (code, msg) = PeakErrorMapper.ToErrorCode((uint)s);
        return Result<Unit>.Fail(code, msg);
    }

    /// <summary>
    /// Map a Core <see cref="BaudRate"/> Name to the matching PEAK
    /// <c>TPCANBaudrate</c> enum. The classic <c>PCANBasic.Initialize</c>
    /// API does not accept the bitrate descriptor string; it only
    /// accepts the four <c>PCAN_BAUD_*</c> presets. Returns null for
    /// any name we don't recognize, which the caller maps to a
    /// <see cref="ErrorCode.HardwareParameter"/> failure.
    /// </summary>
    private static TPCANBaudrate? ResolveClassicCode(BaudRate baud) => baud.Name switch
    {
        "125 kbps" => TPCANBaudrate.PCAN_BAUD_125K,
        "250 kbps" => TPCANBaudrate.PCAN_BAUD_250K,
        "500 kbps" => TPCANBaudrate.PCAN_BAUD_500K,
        "1 Mbps" => TPCANBaudrate.PCAN_BAUD_1M,
        _ => null,
    };
}
