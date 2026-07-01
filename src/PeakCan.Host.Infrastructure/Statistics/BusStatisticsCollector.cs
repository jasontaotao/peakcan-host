using System.Diagnostics;
using PeakCan.Host.Core;
using PeakCan.Host.Infrastructure.Channel;

namespace PeakCan.Host.Infrastructure.Statistics;

/// <summary>
/// Immutable snapshot of bus statistics at a point in time.
/// <para>
/// Returned by <see cref="BusStatisticsCollector.Snapshot"/> and consumed
/// by the stats view. All counts are monotonic since collector creation;
/// rates are computed over the trailing 1-second window.
/// </para>
/// </summary>
/// <param name="TotalFrames">Total frames observed since the collector was created (includes errors).</param>
/// <param name="ErrorFrames">Sub-count of <paramref name="TotalFrames"/> flagged with <see cref="FrameFlags.ErrFrame"/>.</param>
/// <param name="FramesPerSecond">Rolling 1-second window frame rate. Returns <c>0.0</c> when the window is empty.</param>
/// <param name="TotalBytes">Total DLC bytes observed since collector creation.</param>
/// <param name="BytesPerSecond">Rolling 1-second window byte rate. Returns <c>0.0</c> when the window is empty.</param>
/// <param name="BusLoadPercent">Estimated bus load in percent, clamped to <c>[0, 100]</c> via the 8000-fps saturation heuristic.</param>
public sealed record BusStatistics(
    long TotalFrames,
    long ErrorFrames,
    double FramesPerSecond,
    long TotalBytes,
    double BytesPerSecond,
    double BusLoadPercent);

/// <summary>
/// <see cref="IFrameSink"/> that accumulates per-frame counters and
/// maintains a rolling 1-second window for FPS / BPS / bus-load metrics.
/// <para>
/// Thread-safety: every counter read or write and every queue mutation
/// happens under a single <c>_recentLock</c>. <see cref="Snapshot"/> is
/// therefore a coherent point-in-time view — a frame that arrives mid-snapshot
/// is either fully visible in counters + window, or fully invisible. Safe
/// to call <see cref="OnFrame"/> from any thread (SDK read thread, UI
/// thread, or parallel test producers).
/// </para>
/// <para>
/// <see cref="OnError"/> writes a debug-trace line so the notification is
/// observable on a debugger-attached host (mirroring the ChannelRouter
/// pattern at <c>ChannelRouter.cs:121</c>). The collector itself does not
/// act on errors — it is a downstream consumer, not a failure source.
/// </para>
/// </summary>
public sealed class BusStatisticsCollector : IFrameSink
{
    private long _total;
    private long _err;
    private long _bytes;

    // Trailing 1-second sliding window of (tick-at-arrival, frame.Dlc).
    // Sized to ~10k entries worst case (10k fps classic CAN); the
    // Stopwatch tick granularity makes this a soft cap that the trim loop
    // bounds regardless.
    private readonly Queue<(long Ticks, int Bytes)> _recent = new();
    private readonly object _recentLock = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>
    /// Records one received frame. MUST NOT throw — the
    /// <see cref="ChannelRouter"/> invariant is that sinks never throw on
    /// <see cref="IFrameSink.OnFrame"/> (see <c>IFrameSink</c> XML doc).
    /// </summary>
    public void OnFrame(CanFrame frame)
    {
        long now = _clock.ElapsedTicks;
        lock (_recentLock)
        {
            _total++;
            if (frame.IsError)
            {
                _err++;
            }
            _bytes += frame.Dlc;
            _recent.Enqueue((now, frame.Dlc));
            // Trim head: anything older than 1 second relative to *now*
            // is dropped. The locally-captured `now` is safe because the
            // lock is held — no concurrent push can advance the tail past us.
            while (_recent.Count > 0
                   && now - _recent.Peek().Ticks > TimeSpan.TicksPerSecond)
            {
                _recent.Dequeue();
            }
        }
    }

    /// <summary>
    /// Surfaces the forwarded exception via <see cref="Debug.WriteLine"/>
    /// for debugger-attached hosts (mirrors <c>ChannelRouter.cs</c>
    /// pattern). The collector does not act on the error — it is purely
    /// informational: the originating sink's failure is the router's
    /// concern, not this collector's.
    /// </summary>
    public void OnError(Exception ex)
    {
        Debug.WriteLine(
            $"[BusStatisticsCollector] forwarded exception (informational, no action taken): {ex.GetType().Name}: {ex.Message}");
    }

    /// <summary>
    /// Reads current counters and window contents under the same lock
    /// used by <see cref="OnFrame"/>, guaranteeing a coherent snapshot.
    /// </summary>
    public BusStatistics Snapshot()
    {
        lock (_recentLock)
        {
            int count = _recent.Count;
            long bytesInWindow = 0L;
            foreach (var entry in _recent)
            {
                bytesInWindow += entry.Bytes;
            }

            // windowSeconds is a fixed 1.0 whenever the window has any
            // entries: the trim loop in OnFrame guarantees every retained
            // frame is within 1 second of the most recent arrival. This makes
            // FramesPerSecond == count, which is what the trace view displays.
            double windowSeconds = count > 0 ? 1.0 : 0.0;
            double fps = windowSeconds > 0.0 ? count / windowSeconds : 0.0;
            double bps = windowSeconds > 0.0 ? bytesInWindow / windowSeconds : 0.0;

            return new BusStatistics(
                _total,
                _err,
                fps,
                _bytes,
                bps,
                LoadPercent(count));
        }
    }

    /// <summary>
    /// Crude bus-load heuristic: classic 1 Mbps CAN at 100% load sustains
    /// ~8000 fps at an average 8-byte DLC, so we map 8000 fps → 100% via
    /// <c>fps / 80</c> and clamp to <c>[0, 100]</c>.
    /// <para>
    /// <b>Caveats (documented per spec 6.1):</b>
    /// <list type="bullet">
    ///   <item>CAN FD with higher bitrate will under-report (FD can sustain ~30k fps at saturation).</item>
    ///   <item>Non-1 Mbps buses (e.g. 500 kbps) will report inflated load.</item>
    ///   <item>Variable DLC shifts the saturation point; we use 8 as the classic reference.</item>
    /// </list>
    /// A future revision can plumb the active bitrate + average DLC and
    /// compute a real bit-budget percentage instead of this fps heuristic.
    /// </para>
    /// </summary>
    private static double LoadPercent(int framesInWindow)
    {
        return Math.Min(100.0, framesInWindow / 80.0);
    }
}