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
    private static readonly string[] EpisodeParticipants = ["0x500"];

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

    [Fact]
    public void Build_SharedIdParticipants_AreDeduplicated()
    {
        var frames = new[] { F(0), F(0.5) };

        var episode = Assert.Single(NmEpisodeBuilder.Build(frames, 2000, 0, 1));

        Assert.Single(episode.Participants);
        Assert.Equal("0x500", episode.Participants[0]);
    }

    [Fact]
    public void Build_SilenceExactlyAtTimeout_IsNotWindowTruncation()
    {
        // 最后一帧 5.0、窗口 7.0、timeout 2s：gap 恰好等于阈值，按切分判据 episode 已结束。
        var frames = new[] { F(4.5), F(5.0) };

        var episode = Assert.Single(NmEpisodeBuilder.Build(frames, 2000, 0, 7.0));

        Assert.False(episode.BoundaryEnd);
    }

    [Fact]
    public void Build_WindowEndsInsideSilenceBelowTimeout_IsWindowTruncation()
    {
        // 最后一帧 5.0、窗口 6.0、timeout 2s：尚未静默满阈值，窗口提前结束。
        var frames = new[] { F(4.5), F(5.0) };

        var episode = Assert.Single(NmEpisodeBuilder.Build(frames, 2000, 0, 6.0));

        Assert.True(episode.BoundaryEnd);
    }

    [Fact]
    public void Build_SingleFrame_ProducesZeroLengthEpisode()
    {
        var episode = Assert.Single(NmEpisodeBuilder.Build(new[] { F(1.0) }, 2000, 0, 10));

        Assert.Equal(1.0, episode.Start);
        Assert.Equal(1.0, episode.End);
    }

    [Fact]
    public void Build_SortsOutOfOrderFrames()
    {
        var frames = new[] { F(1.0), F(0.5) };

        var episode = Assert.Single(NmEpisodeBuilder.Build(frames, 2000, 0, 10));

        Assert.Equal(0.5, episode.Start);
        Assert.Equal(1.0, episode.End);
    }

    // ---------------- 事件 -> episode 编号 ----------------

    private static NmEpisode[] TwoEpisodes() => new[]
    {
        new NmEpisode(0, 0.0, 1.0, "active", null, EpisodeParticipants, false, false),
        new NmEpisode(1, 3.0, 4.0, "active", null, EpisodeParticipants, false, false),
    };

    [Fact]
    public void IndexAt_InsideEpisode_ReturnsThatEpisode()
    {
        Assert.Equal(0, NmEpisodeBuilder.IndexAt(0.5, TwoEpisodes()));
        Assert.Equal(1, NmEpisodeBuilder.IndexAt(3.5, TwoEpisodes()));
    }

    [Fact]
    public void IndexAt_InGap_ReturnsClosestPreviousEpisode()
    {
        // 空档 1.0..3.0 不属于任何 episode，按“前一个”落到 episode 0。
        Assert.Equal(0, NmEpisodeBuilder.IndexAt(2.0, TwoEpisodes()));
        // 最后一个 episode 之后仍是“前一个”。
        Assert.Equal(1, NmEpisodeBuilder.IndexAt(9.0, TwoEpisodes()));
    }

    [Fact]
    public void IndexAt_BeforeFirstEpisodeOrNoEpisodes_ReturnsZero()
    {
        Assert.Equal(0, NmEpisodeBuilder.IndexAt(-1.0, TwoEpisodes()));
        Assert.Equal(0, NmEpisodeBuilder.IndexAt(1.0, null));
        Assert.Equal(0, NmEpisodeBuilder.IndexAt(1.0, Array.Empty<NmEpisode>()));
    }
}
