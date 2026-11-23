using PeakCan.HIL.Core;
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

public class NmFrameSelectorTests
{
    // CA1861: 常量数组不内联，避免每次构造都分配。
    private static readonly double[] ExpectedTimestamps = [0.5, 1.0, 2.0];

    private static ReplayFrame F(double t, uint id, FrameFlags flags = FrameFlags.None, ushort ch = 0, bool? tx = null)
        => new(t, id, 8, new byte[8], flags, true, ch, tx);

    private static NmAnalysisRequest Request(IReadOnlyList<ReplayFrame> frames, ushort? channel = null,
        double start = 0, double end = double.MaxValue)
        => new("src", channel, frames, null, EmptyProfile(), start, end);

    private static NmProfile EmptyProfile() => new(
        NmProfileSource.None, Array.Empty<NmMessageDef>(), new NmTimings(),
        Array.Empty<NmNodeDef>(), Array.Empty<NmConfigConflict>(), Array.Empty<string>(), new NmProvenance());

    [Fact]
    public void Select_DropsErrorAndRtrFramesAndCountsThem()
    {
        var frames = new[]
        {
            F(0.0, 0x100),
            F(0.1, 0x100, FrameFlags.ErrFrame),
            F(0.2, 0x100, FrameFlags.Rtr),
        };

        var selected = NmFrameSelector.Select(Request(frames), out var diag);

        Assert.Single(selected);
        Assert.Equal(1, diag.ErrorFrames);
        Assert.Equal(1, diag.RtrFrames);
    }

    [Fact]
    public void Select_SortsNonMonotonicTimestampsAndReportsRegression()
    {
        var frames = new[] { F(1.0, 0x100), F(0.5, 0x100), F(2.0, 0x100) };

        var selected = NmFrameSelector.Select(Request(frames), out var diag);

        Assert.True(diag.TimestampRegression);
        // R48：spec §9.1 要的是受影响区间，诊断必须带上第一处回退的时间戳。
        Assert.Equal(0.5, diag.TimestampRegressionAt);
        Assert.Equal(ExpectedTimestamps, selected.Select(f => f.Timestamp));
    }

    // R48：「没回退」与「没记录」必须可区分——单调输入的时间戳位置为 null。
    [Fact]
    public void Select_MonotonicTimestamps_HasNoRegressionTimestamp()
    {
        var frames = new[] { F(1.0, 0x100), F(2.0, 0x100) };

        NmFrameSelector.Select(Request(frames), out var diag);

        Assert.False(diag.TimestampRegression);
        Assert.Null(diag.TimestampRegressionAt);
    }

    [Fact]
    public void Select_MultiChannelWithoutExplicitChannel_ReturnsEmptyAndFlagsIt()
    {
        var frames = new[] { F(0.0, 0x100, ch: 0), F(0.1, 0x100, ch: 1) };

        var selected = NmFrameSelector.Select(Request(frames), out var diag);

        Assert.Empty(selected);
        Assert.True(diag.MultiChannel);
    }

    [Fact]
    public void Select_ExplicitChannel_FiltersToThatChannel()
    {
        var frames = new[] { F(0.0, 0x100, ch: 0), F(0.1, 0x101, ch: 1) };

        var selected = NmFrameSelector.Select(Request(frames, channel: 1), out _);

        Assert.Equal(0x101u, Assert.Single(selected).Id);
    }

    [Fact]
    public void Select_WindowIsHalfOpen()
    {
        var frames = new[] { F(0.9, 0x100), F(1.0, 0x100), F(2.0, 0x100) };

        var selected = NmFrameSelector.Select(Request(frames, start: 1.0, end: 2.0), out _);

        Assert.Single(selected);
        Assert.Equal(1.0, selected[0].Timestamp);
    }
}
