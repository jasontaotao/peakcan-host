// ChannelRouter/FrameRouting.partial.cs — W25 T1 (largest-first order, Flow C)
// Hot-path frame dispatcher: fans a CanFrame out to all registered
// IFrameSink instances with per-sink exception isolation. On a sink
// that throws, logs the original OnFrame exception at Warning
// (EventId 6010) BEFORE invoking OnError. If OnError itself throws,
// logs the secondary exception (EventId 6004) and auto-detaches the
// misbehaving sink via DetachSink (with DetachSink-failure now
// contained — see v3.14.0 MINOR A5 wrapper).
//
// All 3 [LoggerMessage] declarations (LogChannelRouterSinkOnFrameFailed
// + LogSinkOnError + LogDetachSinkFailed) stay on ChannelRouter.cs per
// W18 R1 + W22 D4 + W23 D4 sister precedent (CS8795 mitigation).
//
// Sister of the W18 PeakCanChannel LayerFlow.cs pattern: same
// Infrastructure/Channel namespace, same fan-out-with-error-isolation
// concern shape.
//
// W25 T1 verbatim re-extracted via `git show HEAD:src/...cs | sed -n '183,255p'`
// per W20 T2 R1 fabrication LESSON (21st application).

using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core;

namespace PeakCan.Host.Infrastructure.Channel;

public sealed partial class ChannelRouter
{
    private void OnChannelFrame(CanFrame frame)
    {
        // v3.5.7 PATCH: Volatile.Read on a reference-type field is the
        // canonical acquire-fence read — single load, no constraint
        // gymnastics, no per-frame Interlocked.MemoryBarrier. Replaces
        // the v3.5.5 ReadSinksAcquire helper which had two flaws:
        // (1) post-load barrier placement is not a real acquire fence
        //     (JIT can reorder subsequent reads across the barrier), and
        // (2) the inline comment claimed a write-side fence existed
        //     when in fact AttachSink/DetachSink were plain stores.
        // Both ends now have matching fences via Volatile.Read/Write.
        var sinks = Volatile.Read(ref _sinks) ?? EmptySinks;
        for (var i = 0; i < sinks.Length; i++)
        {
            var s = sinks[i];
            try
            {
                s.OnFrame(frame);
            }
            // OperationCanceledException is allowed to propagate so a sink
            // that is mid-shutdown (per ICanChannel's CTS disposal contract)
            // can abort the read loop cleanly. Other exceptions are caught
            // and rerouted to OnError for per-sink isolation.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // v1.2.13 PATCH Item 9: log the ORIGINAL OnFrame exception BEFORE
                // delegating to OnError. The inner OnError exception (logged by
                // LogSinkOnError below) is the secondary; operators need the root
                // cause from OnFrame to diagnose misbehaving sinks.
                LogChannelRouterSinkOnFrameFailed(_logger, ex, s.GetType().Name);

                // Per-sink isolation: surface the failure to the same sink
                // so it can log. Do not propagate to the channel's read
                // loop (that would silently kill traffic for all sinks).
                try
                {
                    s.OnError(ex);
                }
                catch (Exception onErrorEx)
                {
                    // Per spec section 6.2 ("Never silently swallow errors"),
                    // the secondary exception must be observable. v1.2.12
                    // PATCH Item 11: route through ILogger so Release builds
                    // retain the record (the previous Debug.WriteLine was
                    // stripped when DEBUG was not defined). The original
                    // exception is captured via `when` filter + scope; the
                    // structured exception object carries the full ToString.
                    LogSinkOnError(_logger, onErrorEx, s.GetType().Name);
                    // v3.14.0 MINOR A5: wrap DetachSink in an inner try/catch.
                    // The class xmldoc (lines 67-71) says "do not reorder and
                    // do not wrap DetachSink in another try/catch". The intent
                    // is "the secondary exception must be observable" — that
                    // is satisfied by routing through ILogger (same pattern as
                    // LogSinkOnError directly above). Letting DetachSink
                    // throw would propagate to the channel read loop, count
                    // as a consecutive iteration failure, and after ~100
                    // iterations kill the read loop — apparent CAN bus death
                    // requires Disconnect+Connect to recover. We keep the
                    // observability promise (the exception is logged with its
                    // type name) and the DetachSink call still runs under the
                    // class-level lock, but its failure no longer escapes.
                    try
                    {
                        DetachSink(s);
                    }
                    catch (Exception detachEx)
                    {
                        LogDetachSinkFailed(_logger, detachEx, s.GetType().Name);
                    }
                }
            }
        }
    }
}
