// PeakCanChannel/ConnectFlow.partial.cs — W35 T1 (Flow A, 69 LoC)
// Lifecycle methods: ConnectAsync (gate -> classic/FD Initialize -> start
// read loop) + DisconnectAsync (await read loop + Uninitialize) +
// DisposeAsync (calls DisconnectAsync). All 3 touch _handle + _gate state.
//
// W18 PeakCanChannel NativeBindings.cs has MakeError + ResolveClassicCode
// (also extracted in W18). W35 cross-partial caller: ConnectAsync calls
// MakeError + ResolveClassicCode from NativeBindings.cs partial (W18 sister).
//
// Cross-partial visibility:
//   - ConnectAsync (this partial) reads _handle + _gate + _reader + _logger + Id
//     (all in main partial); calls MakeError + ResolveClassicCode (NativeBindings
//     partial); calls Task.Run + ReadLoopAsync delegate (uses existing W18 read
//     loop by name).
//   - DisconnectAsync reads _handle + _gate (in main); calls await + PCANBasic.
//   - DisposeAsync calls DisconnectAsync (this partial).
//
// 4 [LoggerMessage] declarations (LogReadLoopException + LogReadLoopGivingUp +
// LogReadLoopSubscriberThrew + the 4th implicit static partial) STAY on main
// partial per W18+W22+W23+W25+W26+W27+W28+W29+W30+W31+W32+W33+W34+W35 sister
// precedent (CS8795 mitigation). ConnectFlow does NOT call any logger partial
// directly (no logger calls in ConnectAsync + DisconnectAsync + DisposeAsync
// per main HEAD L109-L227 verification).
//
// W23 STRUCT-FABRACTION LESSON: TPCANStatus enum + uint cast + Result<Unit>
// static factory + ChannelConnectGate.TryEnter + MakeError + ResolveClassicCode
// signatures verified.
//
// W35 T1 verbatim re-extracted via
//   `git show main:src/.../PeakCanChannel.cs | sed -n '109,158p;160,172p;222,227p'`
// per W20 T2 R1 fabrication LESSON (46th application).

using Peak.Can.Basic.BackwardCompatibility;
using PeakCan.Host.Core;

namespace PeakCan.Host.Infrastructure.Peak;

public sealed partial class PeakCanChannel
{
    /// <summary>
    /// Initialize the underlying PCAN-Basic handle at <paramref name="baud"/>
    /// (or its descriptor for FD) and launch the read loop. If <paramref name="fd"/>
    /// is false, <see cref="ResolveClassicCode"/> maps the BaudRate name to the
    /// matching <c>PCAN_BAUD_*</c> preset. Returns <see cref="ErrorCode.HardwareParameter"/>
    /// when the baud rate has no classic CAN preset.
    /// </summary>
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
        // LOW fix: remove unnecessary async/await + Task.FromResult.
        // The method is already async for the ConnectAsync path; this
        // return just avoids the state machine overhead on the success
        // path where no real async work remains.
        return Result<Unit>.Ok(default);
    }

    /// <summary>Stop the read loop and uninitialize the handle. Idempotent.</summary>
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
        catch (Exception) { /* best-effort teardown — catch Exception, not bare catch */ }
        _gate.Dispose();
    }

    /// <summary>DisposeAsync delegates to DisconnectAsync.</summary>
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        // DisposeAsync is now safe to call twice — the gate's CaptureFor-
        // Disconnect returns a null loop on the second call.
    }
}