using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.HIL.Expressions;
using Xunit;

namespace PeakCan.Host.Core.Tests.HIL.Multichannel;

/// <summary>
/// TDD tests for Bug-2: frameCount/frameSeen expression functions must accept an
/// optional channel-name argument (string literal, last position) and route the
/// CountSince query to that channel. Without the argument, behavior is unchanged
/// (channelName=null = default channel, zero regression).
///
/// 语法（单引号字符串字面量，lexer 已支持）：
///   frameCount(id)                    → 默认通道，前向窗口
///   frameCount(id, windowMs)          → 默认通道，滑动窗口
///   frameCount(id, 'bus-b')           → bus-b，前向窗口
///   frameCount(id, windowMs, 'bus-b') → bus-b，滑动窗口
///   frameSeen(id)                     → 默认通道
///   frameSeen(id, 'bus-b')            → bus-b
/// </summary>
public sealed class FrameStatisticsChannelRoutingTests
{
    /// <summary>
    /// Spy IFrameStatistics：记录最近一次 CountSince(id, since, now, channelName)
    /// 调用的 channelName + id，供断言表达式是否正确路由通道。返回可控 count。
    /// </summary>
    private sealed class SpyFrameStatistics : IFrameStatistics
    {
        public long Now => 10_000;
        public string? LastChannelName { get; private set; }
        public CanId LastId { get; private set; }
        public int ReturnCount { get; set; } = 5;

        public int CountSince(CanId id, long since, long now)
            => CountSince(id, since, now, null);

        public int CountSince(CanId id, long since, long now, string? channelName = null)
        {
            LastId = id;
            LastChannelName = channelName;
            return ReturnCount;
        }

        public FrameIntervalStats GetIntervalStats(CanId id, long since, long now)
            => GetIntervalStats(id, since, now, null);

        public FrameIntervalStats GetIntervalStats(CanId id, long since, long now, string? channelName = null)
            => new(2, 9.0, 11.0, 10.0, 2.0);
    }

    private static ExpressionValue Id(uint raw) => ExpressionValue.FromLong(raw);

    [Fact]
    public void FrameCount_WithChannel_RoutesToThatChannel()
    {
        var spy = new SpyFrameStatistics();
        var registry = new FrameStatisticsFunctionRegistry(spy, caseStart: 0);

        var ok = registry.TryInvoke("frameCount", new[] { Id(0x123), ExpressionValue.FromString("bus-b") }, out var result);

        Assert.True(ok);
        Assert.Equal(ExpressionValue.ValueKind.Long, result.Kind);
        Assert.Equal(5L, result.AsLong);
        Assert.Equal("bus-b", spy.LastChannelName);
        Assert.Equal(0x123u, spy.LastId.Raw);
    }

    [Fact]
    public void FrameCount_NoChannel_PassesNull()
    {
        // 零回归：frameCount(id) 不带通道 → channelName=null（默认通道）
        var spy = new SpyFrameStatistics();
        var registry = new FrameStatisticsFunctionRegistry(spy, caseStart: 0);

        var ok = registry.TryInvoke("frameCount", new[] { Id(0x123) }, out var result);

        Assert.True(ok);
        Assert.Equal(5L, result.AsLong);
        Assert.Null(spy.LastChannelName);
    }

    [Fact]
    public void FrameCount_WindowAndChannel_RoutesCorrectly()
    {
        var spy = new SpyFrameStatistics();
        var registry = new FrameStatisticsFunctionRegistry(spy, caseStart: 0);

        var ok = registry.TryInvoke("frameCount",
            new[] { Id(0x123), ExpressionValue.FromLong(1000), ExpressionValue.FromString("bus-b") }, out var result);

        Assert.True(ok);
        Assert.Equal(5L, result.AsLong);
        Assert.Equal("bus-b", spy.LastChannelName);
    }

    [Fact]
    public void FrameSeen_WithChannel_RoutesToThatChannel()
    {
        var spy = new SpyFrameStatistics { ReturnCount = 1 };
        var registry = new FrameStatisticsFunctionRegistry(spy, caseStart: 0);

        var ok = registry.TryInvoke("frameSeen", new[] { Id(0x200), ExpressionValue.FromString("bus-a") }, out var result);

        Assert.True(ok);
        Assert.Equal(ExpressionValue.ValueKind.Bool, result.Kind);
        Assert.True(result.AsBool);
        Assert.Equal("bus-a", spy.LastChannelName);
    }

    [Fact]
    public void FrameCount_Legacy_TwoArgWindow_PassesNull()
    {
        // 零回归：frameCount(id, windowMs) 第 2 参是数字（非字符串）→ 不当通道，channelName=null
        var spy = new SpyFrameStatistics();
        var registry = new FrameStatisticsFunctionRegistry(spy, caseStart: 0);

        var ok = registry.TryInvoke("frameCount", new[] { Id(0x123), ExpressionValue.FromLong(1000) }, out var result);

        Assert.True(ok);
        Assert.Equal(5L, result.AsLong);
        Assert.Null(spy.LastChannelName);
    }

    [Fact]
    public void FrameSeen_Legacy_NoChannel_PassesNull()
    {
        var spy = new SpyFrameStatistics();
        var registry = new FrameStatisticsFunctionRegistry(spy, caseStart: 0);

        var ok = registry.TryInvoke("frameSeen", new[] { Id(0x200) }, out var result);

        Assert.True(ok);
        Assert.Null(spy.LastChannelName);
    }
}
