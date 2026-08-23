using System.Collections.Generic;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.Host.Infrastructure.HIL;

/// <summary>
/// 多通道帧统计（spec §3.4，Task 10）：按逻辑通道名路由 IFrameStatistics 查询到各通道独立的
/// <see cref="FrameStatisticsCollector"/>（每 collector 订阅自己 channel 的 FrameReceived）。
/// channelName null/空 = 默认通道。
///
/// 单通道模式不经过此类型（HeadlessHostBuilder 单通道路径直接注册单 FrameStatisticsCollector）。
/// </summary>
internal sealed class MultiChannelFrameStatistics : IFrameStatistics, IDisposable
{
    private readonly IReadOnlyDictionary<string, FrameStatisticsCollector> _collectors;
    private readonly string _defaultChannelName;

    public MultiChannelFrameStatistics(
        IReadOnlyDictionary<string, FrameStatisticsCollector> collectors,
        string? defaultChannelName = null)
    {
        _collectors = collectors;
        _defaultChannelName = defaultChannelName ?? GetFirstKey(collectors);
    }

    private static string GetFirstKey(IReadOnlyDictionary<string, FrameStatisticsCollector> d)
    {
        foreach (var k in d.Keys) return k;
        throw new ArgumentException("Must provide at least one channel collector.", nameof(d));
    }

    public long Now => Resolve(null).Now;

    public int CountSince(CanId id, long since, long now)
        => Resolve(null).CountSince(id, since, now);

    public int CountSince(CanId id, long since, long now, string? channelName)
        => Resolve(channelName).CountSince(id, since, now);

    public FrameIntervalStats GetIntervalStats(CanId id, long since, long now)
        => Resolve(null).GetIntervalStats(id, since, now);

    public FrameIntervalStats GetIntervalStats(CanId id, long since, long now, string? channelName)
        => Resolve(channelName).GetIntervalStats(id, since, now);

    private FrameStatisticsCollector Resolve(string? channelName)
    {
        var name = string.IsNullOrEmpty(channelName) ? _defaultChannelName : channelName;
        return _collectors.TryGetValue(name, out var c)
            ? c
            : throw new KeyNotFoundException(
                $"FrameStatistics channel '{name}' not found. Available: {string.Join(", ", _collectors.Keys)}");
    }

    public void Dispose()
    {
        foreach (var c in _collectors.Values) c.Dispose();
    }
}
