// ChannelRouter/Sinks.partial.cs — W25 T2 (Flow B)
// Sink-list registration: AttachSink (idempotent add + Volatile.Write
// release-fence publish) + DetachSink (idempotent remove + array
// rebuild + null publish when last sink removed). Both gated by
// the channel-router lock and use Volatile.Write to publish the new
// _sinks array — OnChannelFrame (in FrameRouting.partial.cs) reads
// it via Volatile.Read acquire-fence.
//
// Zero-allocation immutable-snapshot pattern documented at
// ChannelRouter.cs L28-42 (v1.2.3 + v3.5.7 PATCH).
//
// Sister of W22 RecordService/Format.partial.cs pattern: same
// "registration-time mutation gated by lock, per-frame immutable-snapshot
// read via Volatile.Read" concern shape.
//
// W25 T2 verbatim re-extracted via `git show HEAD:src/...cs | sed -n '137,181p'`
// per W20 T2 R1 fabrication LESSON (22nd application).

using System.Collections.Immutable;

namespace PeakCan.Host.Infrastructure.Channel;

public sealed partial class ChannelRouter
{
    public void AttachSink(IFrameSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_gate)
        {
            var current = _sinks ?? EmptySinks;
            // Linear scan is fine: AttachSink runs at registration time
            // (typically 4-5 sinks, once per app lifetime), not per-frame.
            if (Array.IndexOf(current, sink) >= 0) return;
            var next = new IFrameSink[current.Length + 1];
            Array.Copy(current, next, current.Length);
            next[current.Length] = sink;
            // v3.5.7 PATCH: Volatile.Write gives the publish a release
            // fence — OnChannelFrame's Volatile.Read sees the new array
            // with all prior writes (the Array.Copy + last-element
            // assignment) visible.
            Volatile.Write(ref _sinks, next);
        }
    }

    /// <summary>Remove a sink from the fan-out list. Idempotent.</summary>
    public void DetachSink(IFrameSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_gate)
        {
            var current = _sinks;
            if (current is null || current.Length == 0) return;
            var idx = Array.IndexOf(current, sink);
            if (idx < 0) return;
            // Replace the array with a copy that omits the sink. If we
            // just removed the last sink, publish null so OnChannelFrame
            // can fast-path on `_sinks is null` (rare in practice — most
            // apps have at least one persistent sink).
            IFrameSink[]? next = (current.Length == 1)
                ? null
                : new IFrameSink[current.Length - 1];
            if (next is not null)
            {
                Array.Copy(current, 0, next, 0, idx);
                Array.Copy(current, idx + 1, next, idx, current.Length - idx - 1);
            }
            Volatile.Write(ref _sinks, next);
        }
    }
}
