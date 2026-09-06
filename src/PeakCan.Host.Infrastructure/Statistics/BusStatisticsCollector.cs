using System.Diagnostics;
using PeakCan.HIL.Core;
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
/// <param name="BusLoadPercent">Estimated bus load in percent: real bit-budget <c>(frames × overhead bits + DLC bytes × 8) / nominal bitrate</c>, clamped to <c>[0, 100]</c>.</param>
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

    // 2026-09-06 设计层 MEDIUM 修复：总线负载从 "fps/80" 启发式改为真实
    // 位预算（见 LoadPercent 文档）。标称波特率默认 1 Mbps（旧启发式的
    // 隐含假设），由 AppShellViewModel 在连接成功时按所选 BaudRate 预设
    // 更新（见 BaudRateMap）。多通道混合波特率时为"最后一路成功连接的
    // 速率"——聚合口径本身是近似的，文档已注明。
    private long _nominalBitrateBps;

    /// <summary>
    /// Construct the collector. <paramref name="nominalBitrateBps"/> is the
    /// nominal (arbitration-phase) bitrate used as the load denominator;
    /// the default of 1 Mbps matches the legacy fps-heuristic assumption,
    /// so existing callers see a like-for-like basis (not identical
    /// numbers — the formula changed — but the same default bus).
    /// </summary>
    public BusStatisticsCollector(long nominalBitrateBps = 1_000_000)
    {
        if (nominalBitrateBps <= 0)
            throw new ArgumentOutOfRangeException(nameof(nominalBitrateBps));
        _nominalBitrateBps = nominalBitrateBps;
    }

    /// <summary>
    /// Update the nominal bitrate used as the load denominator (e.g. when
    /// the shell connects a channel with a different <c>BaudRate</c>
    /// preset). Takes <c>_recentLock</c> so <see cref="Snapshot"/> never
    /// observes a torn read.
    /// </summary>
    public void SetBitrate(long nominalBitrateBps)
    {
        if (nominalBitrateBps <= 0)
            throw new ArgumentOutOfRangeException(nameof(nominalBitrateBps));
        lock (_recentLock)
        {
            _nominalBitrateBps = nominalBitrateBps;
        }
    }

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
                LoadPercent(count, bytesInWindow, _nominalBitrateBps));
        }
    }

    /// <summary>
    /// Fixed per-frame overhead estimate, in bits, for a standard CAN
    /// frame at the nominal (arbitration) bitrate: ~47 base bits
    /// (SOF + arbitration + control + CRC + ACK + EOF + IFS) plus a
    /// bit-stuffing margin. Deliberately conservative — the goal is an
    /// honest ±20%-class estimate, not bus-analyzer precision.
    /// </summary>
    private const int FrameOverheadBits = 64;

    /// <summary>
    /// Real bit-budget bus load: <c>(frames × overhead + DLC bytes × 8) /
    /// nominal bitrate × 100</c>, clamped to <c>[0, 100]</c>.
    /// <para>
    /// 2026-09-06 设计层 MEDIUM 修复：替换旧的 <c>fps / 80</c> 启发式
    ///（其文档自认 CAN FD 低估、500 kbps 高估、固定 8 字节 DLC——
    /// 即显示值不可信）。新公式使用窗口内的真实 DLC 字节数 + 固定帧
    /// 开销估计，除以标称波特率。
    /// </para>
    /// <para>
    /// <b>Caveats:</b>
    /// <list type="bullet">
    ///   <item>CAN FD：数据段以更高数据相位速率传输，按标称速率折算会
    ///   高估负载（方向与旧启发式相反，但同样存在）；FD 帧的固定开销
    ///   也更高（EGA/CRC/动态填充）。按标称速率归一是有意的保守口径。</item>
    ///   <item>多通道聚合：单个收集器跨所有已注册通道累加，混合波特率
    ///   时分母取最后一路连接的速率——聚合负载本身即近似值。</item>
    ///   <item>错误帧 payload 为 0 字节，按最小帧计 64 开销位进入位预算
    ///（其真实长度协议未定义）；总帧数/FPS 口径不变。</item>
    /// </list>
    /// </para>
    /// </summary>
    private static double LoadPercent(int framesInWindow, long bytesInWindow, long nominalBitrateBps)
    {
        var bits = (long)framesInWindow * FrameOverheadBits + bytesInWindow * 8;
        return Math.Clamp(bits * 100.0 / nominalBitrateBps, 0.0, 100.0);
    }
}