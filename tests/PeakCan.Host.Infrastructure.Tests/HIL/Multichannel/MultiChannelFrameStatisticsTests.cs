using PeakCan.HIL.Core;
using PeakCan.Host.Infrastructure.HIL;
using PeakCan.Host.Infrastructure.Tests.HIL;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Multichannel;

/// <summary>
/// MultiChannelFrameStatistics 路由测试（spec §3.4，Task 10）：
/// 按 channelName 路由 CountSince/GetIntervalStats 到各通道独立 collector；
/// null/空 = 默认通道；未知通道名抛 KeyNotFoundException。
/// 每通道只看到自己 channel 的帧（FakeCanChannel.SimulateFrame 隔离）。
/// </summary>
public sealed class MultiChannelFrameStatisticsTests
{
    private static readonly CanId IdA = new(0x100, FrameFormat.Standard);

    [Fact]
    public void CountSince_RoutesByChannelName_OnlySeesThatChannelsFrames()
    {
        // 两个独立 FakeCanChannel + 各自 collector（可控时钟）
        var clkA = 0L; var clkB = 0L;
        var chA = new FakeCanChannel(0x51);
        var chB = new FakeCanChannel(0x52);
        var colA = new FrameStatisticsCollector(chA, () => clkA);
        var colB = new FrameStatisticsCollector(chB, () => clkB);
        var multi = new MultiChannelFrameStatistics(
            new Dictionary<string, FrameStatisticsCollector> { ["bus-a"] = colA, ["bus-b"] = colB },
            defaultChannelName: "bus-a");

        // 喂帧：bus-a 收 3 帧 0x100，bus-b 收 1 帧 0x100
        clkA = 10; chA.SimulateFrame(new CanFrame(IdA, new byte[] { 0x01 }, FrameFlags.None, chA.Id, default));
        clkA = 20; chA.SimulateFrame(new CanFrame(IdA, new byte[] { 0x02 }, FrameFlags.None, chA.Id, default));
        clkA = 30; chA.SimulateFrame(new CanFrame(IdA, new byte[] { 0x03 }, FrameFlags.None, chA.Id, default));
        clkB = 15; chB.SimulateFrame(new CanFrame(IdA, new byte[] { 0x04 }, FrameFlags.None, chB.Id, default));

        // 查 bus-a [0,30] → 3 帧；bus-b [0,30] → 1 帧（隔离）
        Assert.Equal(3, multi.CountSince(IdA, since: 0, now: 30, channelName: "bus-a"));
        Assert.Equal(1, multi.CountSince(IdA, since: 0, now: 30, channelName: "bus-b"));
        // 3-param（无 channelName）→ 默认 bus-a → 3
        Assert.Equal(3, multi.CountSince(IdA, since: 0, now: 30));
    }

    [Fact]
    public void CountSince_NullOrEmptyChannelName_UsesDefault()
    {
        var clk = 0L;
        var ch = new FakeCanChannel(0x51);
        var col = new FrameStatisticsCollector(ch, () => clk);
        var multi = new MultiChannelFrameStatistics(
            new Dictionary<string, FrameStatisticsCollector> { ["bus-a"] = col },
            defaultChannelName: "bus-a");

        clk = 5; ch.SimulateFrame(new CanFrame(IdA, new byte[] { 0x01 }, FrameFlags.None, ch.Id, default));
        Assert.Equal(1, multi.CountSince(IdA, since: 0, now: 5, channelName: null));
        Assert.Equal(1, multi.CountSince(IdA, since: 0, now: 5, channelName: ""));
    }

    [Fact]
    public void CountSince_UnknownChannelName_Throws()
    {
        var ch = new FakeCanChannel(0x51);
        var col = new FrameStatisticsCollector(ch);
        var multi = new MultiChannelFrameStatistics(
            new Dictionary<string, FrameStatisticsCollector> { ["bus-a"] = col },
            defaultChannelName: "bus-a");

        Assert.Throws<KeyNotFoundException>(() =>
        {
            multi.CountSince(IdA, since: 0, now: 10, channelName: "bus-x");
        });
    }

    [Fact]
    public void GetIntervalStats_RoutesByChannelName()
    {
        var clkA = 0L; var clkB = 0L;
        var chA = new FakeCanChannel(0x51);
        var chB = new FakeCanChannel(0x52);
        var colA = new FrameStatisticsCollector(chA, () => clkA);
        var colB = new FrameStatisticsCollector(chB, () => clkB);
        var multi = new MultiChannelFrameStatistics(
            new Dictionary<string, FrameStatisticsCollector> { ["bus-a"] = colA, ["bus-b"] = colB },
            defaultChannelName: "bus-a");

        // bus-a: 3 帧间隔 10ms；bus-b: 2 帧间隔 5ms
        clkA = 10; chA.SimulateFrame(new CanFrame(IdA, new byte[] { 0x01 }, FrameFlags.None, chA.Id, default));
        clkA = 20; chA.SimulateFrame(new CanFrame(IdA, new byte[] { 0x02 }, FrameFlags.None, chA.Id, default));
        clkA = 30; chA.SimulateFrame(new CanFrame(IdA, new byte[] { 0x03 }, FrameFlags.None, chA.Id, default));
        clkB = 5; chB.SimulateFrame(new CanFrame(IdA, new byte[] { 0x04 }, FrameFlags.None, chB.Id, default));
        clkB = 10; chB.SimulateFrame(new CanFrame(IdA, new byte[] { 0x05 }, FrameFlags.None, chB.Id, default));

        var sa = multi.GetIntervalStats(IdA, since: 0, now: 30, channelName: "bus-a");
        var sb = multi.GetIntervalStats(IdA, since: 0, now: 30, channelName: "bus-b");
        Assert.Equal(3, sa.SampleCount);
        Assert.Equal(10, sa.MinMs);
        Assert.Equal(2, sb.SampleCount);
        Assert.Equal(5, sb.MinMs);
    }
}
