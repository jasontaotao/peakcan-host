using System.Collections.Concurrent;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.Host.Infrastructure.HIL;

/// <summary>
/// <see cref="IFrameStatistics"/> 实现：订阅 <see cref="ICanChannel.FrameReceived"/>，
/// 按 CAN ID 索引 <see cref="ConcurrentQueue{T}"/>（弱一致，读安全）。单调时钟打点
/// （<see cref="Environment.TickCount64"/>，ms）。
/// 查询时懒淘汰过期帧（同时裁剪到 since 边界与 retention 上限），防止长时间不查询时无限增长。
/// </summary>
public sealed class FrameStatisticsCollector : IFrameStatistics, IDisposable
{
    /// <summary>每 CAN ID 保留的最近时间窗口（ms）。超过的帧在查询时被淘汰。</summary>
    public const int RetentionMs = 5000;

    private readonly ConcurrentDictionary<uint, ConcurrentQueue<long>> _framesByKey = new();
    private readonly ICanChannel _channel;
    private readonly Func<long> _now;
    private readonly IDisposable _subscription;
    private int _disposed;

    /// <param name="channel">帧来源。</param>
    /// <param name="now">单调时钟提供者（默认 <see cref="Environment.TickCount64"/>）。测试注入可控时钟。</param>
    public FrameStatisticsCollector(ICanChannel channel, Func<long>? now = null)
    {
        _channel = channel;
        _now = now ?? (() => Environment.TickCount64);
        _subscription = new FrameReceivedSubscription(channel, OnFrame);
    }

    public long Now => _now();

    /// <summary>[since, now] 区间内指定 CAN ID 的帧计数。</summary>
    public int CountSince(CanId id, long since, long now)
    {
        var queue = GetQueue(id);
        if (queue is null) return 0;

        Evict(queue, since);
        int count = 0;
        foreach (var ticks in queue)
            if (ticks >= since && ticks <= now) count++;
        return count;
    }

    public FrameIntervalStats GetIntervalStats(CanId id, long since, long now)
    {
        var queue = GetQueue(id);
        if (queue is null)
            return new FrameIntervalStats(0, 0, 0, 0, 0);

        Evict(queue, since);
        var ticks = queue.Where(t => t >= since && t <= now).OrderBy(t => t).ToList();
        if (ticks.Count < 2)
            return new FrameIntervalStats(ticks.Count, 0, 0, 0, 0);

        var intervals = new double[ticks.Count - 1];
        for (int i = 1; i < ticks.Count; i++)
            intervals[i - 1] = ticks[i] - ticks[i - 1];   // 时钟单位 ms
        return new FrameIntervalStats(
            ticks.Count,
            intervals.Min(),
            intervals.Max(),
            intervals.Average(),
            intervals.Max() - intervals.Min());
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _subscription.Dispose();
    }

    private void OnFrame(CanFrame frame) => _framesByKey.GetOrAdd(ToKey(frame.Id), _ => new ConcurrentQueue<long>())
        .Enqueue(_now());

    private ConcurrentQueue<long>? GetQueue(CanId id)
        => _framesByKey.TryGetValue(ToKey(id), out var queue) ? queue : null;

    /// <summary>懒淘汰：cutoff 取 <paramref name="since"/> 与 retention 边界<strong>较小者</strong>——
    /// 窗口语义优先（窗口内帧绝不被淘汰），retention 只在窗口短于 RetentionMs 时兜底清理旧帧。
    /// 修复 review H1：原 max() 在 WindowMs &gt; 5s 时 retention 覆盖 since 截断窗口，导致错误断言。</summary>
    private void Evict(ConcurrentQueue<long> queue, long since)
    {
        long cutoff = Math.Min(since, _now() - RetentionMs);
        while (queue.TryPeek(out var ticks) && ticks < cutoff)
            queue.TryDequeue(out _);
    }

    /// <summary>extended bit 映射到 key 高位（与 DBC lookup 一致），避免 standard/extended 同 raw 冲突。</summary>
    private static uint ToKey(CanId id) => id.IsExtended ? id.Raw | 0x80000000u : id.Raw;
}
