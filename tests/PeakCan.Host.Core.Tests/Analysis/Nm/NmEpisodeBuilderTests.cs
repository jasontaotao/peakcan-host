using PeakCan.HIL.Core;
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

public class NmEpisodeBuilderTests
{
    // CA1861: 常量数组不内联，避免每次构造都分配。
    private static readonly string[] TestReasons = ["test"];
    private static readonly byte[] Payload = [0, 0];

    private static NmFrame F(double t, string id = "0x500")
        => new(new NmMessageDef(id, false, "NM", "A", 8, true, TestReasons),
               new ReplayFrame(t, 0x500, 2, Payload, FrameFlags.None));

    [Fact]
    public void Build_SplitsOnTimeoutSilence()
    {
        // 首帧不从 0 开始：这样 BoundaryStart 的判定才可区分（trace 开头不是活动）。
        var frames = new[] { F(0.1), F(0.6), F(1.1), F(4.0), F(4.5) };

        var episodes = NmEpisodeBuilder.Build(frames, timeoutMs: 2000, windowStart: 0, windowEnd: 5);

        Assert.Equal(2, episodes.Count);
        Assert.Equal(0.1, episodes[0].Start);
        Assert.Equal(1.1, episodes[0].End);
        Assert.Equal(4.0, episodes[1].Start);
        Assert.False(episodes[0].BoundaryStart);
    }

    [Fact]
    public void Build_MarksBoundaryWhenWindowStartsWithOrEndsInsideActivity()
    {
        var frames = new[] { F(0), F(0.5) };

        var episodes = NmEpisodeBuilder.Build(frames, 2000, 0, 0.6);

        Assert.True(Assert.Single(episodes).BoundaryStart);
        Assert.True(episodes[0].BoundaryEnd);
    }

    [Fact]
    public void Build_NoFrames_ReturnsEmpty()
    {
        Assert.Empty(NmEpisodeBuilder.Build(Array.Empty<NmFrame>(), 2000, 0, 10));
    }

    [Fact]
    public void Build_SharedIdParticipants_AreReportedByCanId()
    {
        var frames = new[] { F(0), F(0.5) };

        var episode = Assert.Single(NmEpisodeBuilder.Build(frames, 2000, 0, 1));

        Assert.Contains("0x500", episode.Participants);
    }
}
