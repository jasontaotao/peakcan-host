using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// spec §7.2：normal_resume_shape 判「静默后恢复为正常周期发送」。
/// P 级原语，finding 必须标推断（唤醒源不可观测）。
/// </summary>
public class NmRuleEvaluatorsResumeTests
{
    private static readonly string[] TestReasons = ["MsgType=CANNM"];

    // CA1861：常量数组作为实参会在每次调用时新建，提到字段只建一次。
    private static readonly byte[] Payload = [0xF4, 0x00, 0x00, 0x01, 0, 0, 0, 0];

    [Fact]
    public void Resume_NormalCycleAfterSilence_NoFinding()
    {
        // 0–1s 发送 → 1–5s 静默（≥2s）→ 5s 起以 500ms 恢复 5 帧。合规。
        var frames = Frames([0.0, 0.5, 1.0], [5.0, 5.5, 6.0, 6.5, 7.0]);

        var result = Evaluate(frames, minSilenceMs: 2000, cycleMs: 500, pct: 0.1, minFrames: 4);

        Assert.Empty(result.Findings);
        Assert.Null(result.UnmodeledReason);
    }

    [Fact]
    public void Resume_TooFewFramesAfterSilence_ProducesFinding()
    {
        // 恢复后只发 2 帧，低于 min_frames=4：无法证明「恢复为正常周期发送」。
        var frames = Frames([0.0, 0.5, 1.0], [5.0, 5.5]);

        var result = Evaluate(frames, minSilenceMs: 2000, cycleMs: 500, pct: 0.1, minFrames: 4);

        var f = Assert.Single(result.Findings);
        Assert.Equal("NM_NORMAL_RESUME_SHAPE_MISMATCH", f.Code);
        Assert.True(f.IsInference, "P 级原语必须标推断");
    }

    [Fact]
    public void Resume_WrongCycleAfterSilence_ProducesFinding()
    {
        // 恢复后间隔 100ms，远低于 500ms ±10%：这是快发，不是正常周期发送。
        var frames = Frames([0.0, 0.5, 1.0], [5.0, 5.1, 5.2, 5.3, 5.4]);

        var result = Evaluate(frames, minSilenceMs: 2000, cycleMs: 500, pct: 0.1, minFrames: 4);

        Assert.Contains(result.Findings, f => f.Code == "NM_NORMAL_RESUME_SHAPE_MISMATCH");
    }

    [Fact]
    public void Resume_SecondSilenceMalformed_ProducesFindingAtSecondResume()
    {
        // 两次睡眠/唤醒：第一次恢复合规（500ms × 4 帧），
        // 第二次恢复为 100ms 快发。只判第一段静默会漏掉这次违规。
        var frames = Frames(
            [0.0, 0.5, 1.0, 5.0, 5.5, 6.0, 6.5],
            [10.0, 10.1, 10.2, 10.3]);

        var result = Evaluate(frames, minSilenceMs: 2000, cycleMs: 500, pct: 0.1, minFrames: 4);

        var f = Assert.Single(result.Findings);
        Assert.Equal("NM_NORMAL_RESUME_SHAPE_MISMATCH", f.Code);
        // 证据必须落在第二次恢复（10.0s），证明第二个静默点确实被评为违规。
        Assert.Equal(10.0, Assert.Single(f.EvidenceTimes), precision: 6);
    }

    [Fact]
    public void Resume_NoLongSilence_IsUnmodeled()
    {
        // 全程连续发送，没有 ≥2s 的静默：这条规则在本 trace 上不适用。
        // 必须显式进 Unmodeled，不得静默通过。
        var frames = Frames([0.0, 0.5, 1.0, 1.5, 2.0], []);

        var result = Evaluate(frames, minSilenceMs: 2000, cycleMs: 500, pct: 0.1, minFrames: 4);

        Assert.Empty(result.Findings);
        Assert.NotNull(result.UnmodeledReason);
    }

    private readonly record struct Outcome(
        IReadOnlyList<NmFinding> Findings, string? UnmodeledReason);

    private static Outcome Evaluate(
        ReplayFrame[] frames, int minSilenceMs, int cycleMs, double pct, int minFrames)
    {
        var profile = Profile();
        var rule = new NmRuleDef(
            "NM_RESUME_TO_RMS",
            "静默后恢复为正常周期发送",
            "wakeup",
            new NmRuleWhen("normal_resume_shape",
                MinSilenceMs: minSilenceMs, CycleMs: cycleMs, Pct: pct, MinFrames: minFrames),
            new NmRuleExpect("equals"),
            "NM_NORMAL_RESUME_SHAPE_MISMATCH")
        {
            SourceRef = "§5.3.2",
            Clauses = ["NM_02", "NM_15"],
            Observability = NmObservability.Partial,
        };

        var nmFrames = frames
            .Select(f => new NmFrame(
                new NmMessageDef("0x18FFFAF4", true, "NM", "A", 8, true,
                    TestReasons, CycleMs: 500, TimeoutMs: 2000), f))
            .ToArray();
        var episodes = NmEpisodeBuilder.Build(nmFrames, 2000, 0, 12.0);
        var ctx = new NmRuleContext(nmFrames, episodes, profile,
            NmTimingDefaults.FromProfile(profile.Timings), new NmWindow(0, 12.0, "full"));

        var findings = new List<NmFinding>();
        var observations = new List<NmObservation>();
        var implemented = NmRuleEvaluators.TryEvaluate(
            rule, ctx, findings, observations, out var reason);

        Assert.True(implemented, "normal_resume_shape 必须在 v1 闭集内有实现");
        return new Outcome(findings, reason);
    }

    private static ReplayFrame[] Frames(double[] before, double[] after)
    {
        var all = before.Concat(after).OrderBy(t => t).ToArray();
        return all.Select(t => new ReplayFrame(t, 0x18FFFAF4, 8,
            Payload, PeakCan.HIL.Core.FrameFlags.None, IsExtended: true)).ToArray();
    }

    private static NmProfile Profile()
        => new(NmProfileSource.Dbc,
            [new NmMessageDef("0x18FFFAF4", true, "NM", "A", 8, true,
                TestReasons, CycleMs: 500, TimeoutMs: 2000)],
            new NmTimings(CycleMs: 500, TimeoutMs: 2000),
            [new NmNodeDef("A", StationAddress: 0xF4, IsNmNode: true)],
            Array.Empty<NmConfigConflict>(), Array.Empty<string>(), new NmProvenance(),
            Layout: new NmLayout(NidByte: 0, CbvByte: 1),
            Features: new NmFeatures(PnEnabled: false));
}
