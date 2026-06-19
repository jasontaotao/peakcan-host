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
/// Thread-safety: counter updates use <see cref="Interlocked"/>; the
/// window is guarded by a private <c>lock</c>. Safe to call
/// <see cref="OnFrame"/> from any thread (the SDK read thread, UI thread,
/// or parallel test producers).
/// </para>
/// <para>
/// <see cref="OnError"/> is intentionally a no-op: this collector is a
/// downstream consumer, not a source of failures. The
/// <see cref="ChannelRouter"/> forwards per-sink exceptions to the
/// originating sink's <c>OnError</c>; if this collector ever throws, that
/// router-level path will handle the notification, not the other way
/// around.
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
        Interlocked.Increment(ref _total);
        if (frame.IsError)
        {
            Interlocked.Increment(ref _err);
        }
        Interlocked.Add(ref _bytes, frame.Dlc);

        long now = _clock.ElapsedTicks;
        lock (_recentLock)
        {
            _recent.Enqueue((now, frame.Dlc));
            // Trim head: anything older than 1 second relative to *now*
            // is dropped. Re-checking now each iteration is safe because
            // the lock is held — no concurrent push can advance the tail
            // past us.
            while (_recent.Count > 0
                   && now - _recent.Peek().Ticks > TimeSpan.TicksPerSecond)
            {
                _recent.Dequeue();
            }
        }
    }

    /// <summary>
    /// No-op for this collector. The <see cref="ChannelRouter"/> uses
    /// <c>OnError</c> to forward per-sink failures back to the sink that
    /// raised them; <see cref="BusStatisticsCollector"/> does not surface
    /// failures from other sinks, so nothing to log here.
    /// </summary>
    public void OnError(Exception ex)
    {
        // Intentional no-op — see XML doc above.
        _ = ex;
    }

    /// <summary>
    /// Reads current counters and window contents atomically (from the
    /// caller's perspective) and returns a <see cref="BusStatistics"/>.
    /// <para>
    /// Counter reads use <see cref="Interlocked.Read(ref long)"/>; the
    /// window read takes the lock. The two reads are NOT atomic with each
    /// other — a frame that arrives between them will appear in the
    /// counter but not in the window. Acceptable for MVP: the next
    /// snapshot a UI tick later will be self-consistent.
    /// </para>
    /// </summary>
    public BusStatistics Snapshot()
    {
        long total = Interlocked.Read(ref _total);
        long err = Interlocked.Read(ref _err);
        long bytes = Interlocked.Read(ref _bytes);

        int count;
        long bytesInWindow;
        lock (_recentLock)
        {
            count = _recent.Count;
            bytesInWindow = _recent.Sum(x => (long)x.Bytes);
        }

        // windowSeconds is a fixed 1.0 whenever the window has any
        // entries: the trim loop in OnFrame guarantees every retained
        // frame is within 1 second of the most recent arrival. This makes
        // FramesPerSecond == count, which is what the trace view displays.
        double windowSeconds = count > 0 ? 1.0 : 0.0;
        double fps = windowSeconds > 0.0 ? count / windowSeconds : 0.0;
        double bps = windowSeconds > 0.0 ? bytesInWindow / windowSeconds : 0.0;

        return new BusStatistics(
            total,
            err,
            fps,
            bytes,
            bps,
            LoadPercent(count));
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
    private static double LoadPercent(int framesPerSecond)
    {
        return Math.Min(100.0, framesPerSecond / 80.0);
    }
}