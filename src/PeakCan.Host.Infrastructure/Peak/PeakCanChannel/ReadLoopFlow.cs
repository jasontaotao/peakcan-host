using Microsoft.Extensions.Logging;
using Peak.Can.Basic.BackwardCompatibility;
using PeakCan.Host.Core;

namespace PeakCan.Host.Infrastructure.Peak;

public sealed partial class PeakCanChannel
{
    // Flow A: ReadLoopFlow (v3.16.9.4 PATCH + earlier).
    // ReadLoopAsync (single largest method, 75 LoC) + SafeEmitReadLoopError (~16 LoC).
    // Both share the read-loop's event-emission + per-subscriber try/catch isolation pattern.
    // Sister of W14 D2 lifecycle-cluster (mutable-state coupling on _reader/_handle/_gate).
    //
    // Cross-flow callers (partial-class visible):
    //   - ReadLoopAsync -> EmitClassic + EmitFd (Flow B NativeBindings)
    //   - ReadLoopAsync -> LogReadLoopException + LogReadLoopGivingUp + LogReadLoopSubscriberThrew (Flow A also accesses these from elsewhere via cross-partial)
    //   - SafeEmitReadLoopError -> ReadLoopError event (main) + LogReadLoopSubscriberThrew (main)
    // 3 LoggerMessage partials (Flow-A-related) STAY IN MAIN per W10+W11+W12+W13+W14+W15+W17 sister-lesson (source-generator scope requirement).

    /// <summary>
    /// Read-loop body. Polls <c>PCANBasic.Read</c> + <c>PCANBasic.ReadFD</c>
    /// each iteration under separate try/catch blocks (v3.16.9.4) so a
    /// thrown subscriber on classic path doesn't drop FD frames. Surfaces
    /// each kind of failure as a <see cref="ReadLoopError"/> via
    /// <see cref="SafeEmitReadLoopError"/> (per-subscriber isolation).
    /// Gives up after <see cref="MaxConsecutiveReadFailures"/> consecutive
    /// iterations with no frames seen (bus-dead heuristic).
    /// </summary>
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
                while (_reader.ReadClassic(_handle, out var msg, out var ts) == TPCANStatus.PCAN_ERROR_OK)
                {
                    EmitClassic(msg, ts);
                    gotAnyFrame = true;
                }
            }
            catch (Exception ex)
            {
                LogReadLoopException(_logger, Id.Handle, "classic", ex);
                // v3.16.9.4 PATCH: surface to UI in addition to ILogger.
                // Bus-off / driver unload typically throws on the classic
                // path first; the FD path is rarely reached in that state.
                SafeEmitReadLoopError(new ReadLoopError(Id.Handle, ReadLoopErrorKind.ClassicReadException, ex));
                iterationFailed = true;
            }
            try
            {
                while (_reader.ReadFd(_handle, out var fdMsg, out var tsMicroseconds) == TPCANStatus.PCAN_ERROR_OK)
                {
                    EmitFd(fdMsg, tsMicroseconds);
                    gotAnyFrame = true;
                }
            }
            catch (Exception ex)
            {
                LogReadLoopException(_logger, Id.Handle, "FD", ex);
                // v3.16.9.4 PATCH: surface to UI in addition to ILogger.
                SafeEmitReadLoopError(new ReadLoopError(Id.Handle, ReadLoopErrorKind.FdReadException, ex));
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
                // v3.16.9.4 PATCH: notify UI the loop has abandoned. No
                // Exception carried here — the per-iteration catch above
                // already surfaced the underlying cause. Subscribers should
                // interpret LoopGivingUp as "channel is effectively dead,
                // user must Disconnect+Connect to recover".
                SafeEmitReadLoopError(new ReadLoopError(Id.Handle, ReadLoopErrorKind.LoopGivingUp, null));
                return;
            }

            var delay = consecutiveIterationsWithFailure == 0
                ? 1
                : ReadLoopBackoffMs[Math.Min(consecutiveIterationsWithFailure - 1, ReadLoopBackoffMs.Length - 1)];
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// v3.16.9.4 PATCH: invoke <see cref="ReadLoopError"/> with a per-subscriber
    /// try/catch so a misbehaving subscriber (e.g. a UI handler that throws on
    /// a disposed Dispatcher) cannot crash the SDK read loop. Mirrors the
    /// sink-OnError isolation pattern in <c>ChannelRouter</c>: the loop is
    /// the high-priority thread, the subscriber is best-effort.
    /// </summary>
    private void SafeEmitReadLoopError(ReadLoopError err)
    {
        var handler = ReadLoopError;
        if (handler is null) return;
        // Invoke per-subscriber via GetInvocationList so one bad handler
        // does not prevent the next from firing (ChannelRouter contract).
        foreach (Action<ReadLoopError> sub in handler.GetInvocationList())
        {
            try { sub(err); }
            catch (Exception ex)
            {
                LogReadLoopSubscriberThrew(_logger, Id.Handle, sub.Method.DeclaringType?.FullName ?? "?", ex);
            }
        }
    }
}
