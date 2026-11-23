using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// spec §7.1.2：静默必须多态。真实 trace 的全网 NM 静默（9.714–12.677s）
/// 不得被判成单节点违规，否则 AI 会把它当故障解释。
/// </summary>
public class NmSilenceClassifierTests
{
    private static readonly string[] TestReasons = ["test"];

    [Fact]
    public void Classify_LastFrameNearWindowEnd_NotSilent()
    {
        var frames = new[] { Frame(0.0, 0x500), Frame(0.5, 0x500) };

        var verdict = NmSilenceClassifier.Classify(
            frames, "0x500", false, lastSeen: 0.5, episodeEnd: 0.5,
            windowEnd: 0.6, timeoutSec: 2.0);

        Assert.Equal(NmSilenceVerdict.NotSilent, verdict);
    }

    [Fact]
    public void Classify_SilentWhileOthersActive_ReportsNodeSilence()
    {
        // 0x500 在 0.5s 停发，0x510 一直发到 3.0s → 单节点静默，可疑。
        var frames = new[] { Frame(0.0, 0x500), Frame(0.5, 0x500), Frame(2.9, 0x510) };

        var verdict = NmSilenceClassifier.Classify(
            frames, "0x500", false, lastSeen: 0.5, episodeEnd: 3.0,
            windowEnd: 3.0, timeoutSec: 2.0);

        Assert.Equal(NmSilenceVerdict.SilentWithOtherNmActive, verdict);
    }

    [Fact]
    public void Classify_AllNmSilent_ReportsAllSilentNotNodeViolation()
    {
        // 全部 NM 在 0.5s 停发，窗口到 3.0s → 全网静默，原因未知。
        var frames = new[] { Frame(0.0, 0x500), Frame(0.5, 0x500), Frame(0.5, 0x510) };

        var verdict = NmSilenceClassifier.Classify(
            frames, "0x500", false, lastSeen: 0.5, episodeEnd: 0.5,
            windowEnd: 3.0, timeoutSec: 2.0);

        Assert.Equal(NmSilenceVerdict.AllNmSilent, verdict);
    }

    [Fact]
    public void Classify_SharedId_IsIndeterminate()
    {
        // 同一 CAN ID 下两个 NID 交替：按报文拆不出节点，不能判单节点静默。
        var frames = new[]
        {
            Frame(0.0, 0x500, nid: 0x11), Frame(0.5, 0x500, nid: 0x22),
            Frame(1.0, 0x500, nid: 0x11), Frame(2.9, 0x510),
        };

        var verdict = NmSilenceClassifier.Classify(
            frames, "0x500", false, lastSeen: 1.0, episodeEnd: 3.0,
            windowEnd: 3.0, timeoutSec: 2.0, nidByte: 0);

        Assert.Equal(NmSilenceVerdict.IndeterminateAtBoundary, verdict);
    }

    [Fact]
    public void Classify_EpisodeEndsAtWindowEnd_IsIndeterminate()
    {
        // episode 自身被窗口切断：末帧之后到窗口末尾的静默无法判明。
        var frames = new[] { Frame(0.0, 0x500), Frame(0.5, 0x500) };

        var verdict = NmSilenceClassifier.Classify(
            frames, "0x500", false, lastSeen: 0.5, episodeEnd: 0.5,
            windowEnd: 0.5, timeoutSec: 2.0, episodeTruncated: true);

        Assert.Equal(NmSilenceVerdict.IndeterminateAtBoundary, verdict);
    }

    [Fact]
    public void SilentBeyondTimeout_IsDerivedFromVerdict()
    {
        // spec §7.1.2：兼容属性由 Verdict 派生，不可独立赋值。
        var allSilent = new NmMessageReport(
            "0x500", false, "NM", "A", 2, 0, 0.5,
            new NmCycleStats(null, null, null, null, null, 0),
            Array.Empty<NmSegment>(),
            SilenceVerdict: NmSilenceVerdict.AllNmSilent);

        var nodeSilent = allSilent with { SilenceVerdict = NmSilenceVerdict.SilentWithOtherNmActive };

        Assert.False(allSilent.SilentBeyondTimeout);
        Assert.True(nodeSilent.SilentBeyondTimeout);
    }

    private static NmFrame Frame(double t, uint id, byte nid = 0x11)
        => new(
            new NmMessageDef($"0x{id:X}", false, "NM", "A", 8, true, TestReasons),
            new ReplayFrame(t, id, 8, new byte[] { nid, 0, 0, 0, 0, 0, 0, 0 },
                PeakCan.HIL.Core.FrameFlags.None));
}
