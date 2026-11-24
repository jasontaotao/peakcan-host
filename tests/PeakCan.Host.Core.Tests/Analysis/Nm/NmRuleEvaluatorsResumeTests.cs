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

    // spec §4.1：停发本身不是违规（准备睡眠状态下停发是规范要求的）。
    // stop_sending 只产 observation，绝不产 finding。
    [Fact]
    public void StopSending_NodeStopsWhileOthersActive_ProducesObservationNotFinding()
    {
        // A 在 2s 后停发，B 持续到 8s。
        var frames = new List<ReplayFrame>();
        foreach (var t in new[] { 0.0, 0.5, 1.0, 1.5 })
            frames.Add(FrameAt(t, 0x18FFFAF4, 0xF4));
        foreach (var t in new[]
                 {
                     0.0, 0.5, 1.0, 1.5, 2.0, 2.5, 3.0, 3.5, 4.0,
                     4.5, 5.0, 5.5, 6.0, 6.5, 7.0, 7.5, 8.0,
                 })
            frames.Add(FrameAt(t, 0x18FFFAF5, 0xF5));

        var result = EvaluateStopSending(frames.ToArray());

        // 零 finding 是**结构性**保证：StopSending 的签名根本不接收 findings 列表，
        // 此断言只防未来把列表接进来，不构成行为覆盖。
        Assert.Empty(result.Findings);

        var aStop = Assert.Single(result.Observations,
            o => o.Kind == "stop_sending" && o.CanIdHex == "0x18FFFAF4");
        // 末帧 NMSts=1 → 合规释放：这是实测事实，不是推断。
        // 若 lastFrameInReadySleep 没被传（brief 的原始缺陷），归因会退回
        // SilentWithOtherNmActive，此断言即变红。
        Assert.False(aStop.IsInference);
        Assert.Equal("measured", aStop.EvidenceKind);

        // B 发送到 8.0s、窗口到 9.0s（间隔 < 超时）：B 从未停发，不应有停发观察。
        Assert.DoesNotContain(result.Observations,
            o => o.Kind == "stop_sending" && o.CanIdHex == "0x18FFFAF5");
    }

    private readonly record struct Outcome(
        IReadOnlyList<NmFinding> Findings, string? UnmodeledReason, string? UnmodeledCode);

    private readonly record struct StopOutcome(
        IReadOnlyList<NmFinding> Findings,
        IReadOnlyList<NmObservation> Observations,
        string? UnmodeledReason);

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
            rule, ctx, findings, observations, out var reason, out var code);

        Assert.True(implemented, "normal_resume_shape 必须在 v1 闭集内有实现");
        return new Outcome(findings, reason, code);
    }

    // ==== spec §7.2 / §19.2：expect_nmsts 区分 NM_02/15（RMS）与 NM_10（NOS） ====

    [Fact]
    public void Resume_ExpectNmstsMatches_NoFinding()
    {
        // 恢复后 NMSts=1，规则期望 1（NM_10 的目标状态）：目标状态一致，不产 finding。
        var frames = FramesWithNmsts(
            before: [(0.0, 0), (0.5, 0), (1.0, 0)],
            after: [(5.0, 1), (5.5, 1), (6.0, 1), (6.5, 1), (7.0, 1)]);

        var result = EvaluateWithNmsts(frames, expectNmsts: 1);

        Assert.Empty(result.Findings);
        Assert.Null(result.UnmodeledReason);
    }

    [Fact]
    public void Resume_ExpectNmstsMismatch_ProducesFinding()
    {
        // 恢复后 NMSts=0（RMS），但规则期望 1（NM_10 的 NOS）：目标状态不符。
        var frames = FramesWithNmsts(
            before: [(0.0, 0), (0.5, 0), (1.0, 0)],
            after: [(5.0, 0), (5.5, 0), (6.0, 0), (6.5, 0), (7.0, 0)]);

        var result = EvaluateWithNmsts(frames, expectNmsts: 1);

        var f = Assert.Single(result.Findings);
        Assert.Equal("NM_NORMAL_RESUME_SHAPE_MISMATCH", f.Code);
        Assert.True(f.IsInference, "P 级原语必须标推断");
        Assert.Contains("NMSts", f.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resume_ExpectNmstsButRoleMissing_IsUnmodeledAndStillChecksCycle()
    {
        // 规则要求校验目标状态，但 profile 没声明 NMSts 位角色：
        // 目标状态不能静默当作「一致」，必须显式进 Unmodeled。
        // 注意本用例的帧周期合规，所以没有 finding；「缺角色时周期判据仍执行」
        // 由 Resume_MissingNmstsRole_StillReportsCycleViolation 单独证明。
        var frames = Frames([0.0, 0.5, 1.0], [5.0, 5.5, 6.0, 6.5, 7.0]);

        var result = EvaluateWithNmsts(frames, expectNmsts: 1, declareNmstsRole: false);

        Assert.Empty(result.Findings);
        Assert.NotNull(result.UnmodeledReason);
        Assert.Contains("NmSts 位角色", result.UnmodeledReason, StringComparison.Ordinal);
    }

    // MEDIUM-1 的回归守卫：缺 NmSts 位角色时，周期判据仍必须执行。
    // 原实现缺角色就整条 return Unmodeled，把已确证的周期违规一起吞掉——
    // 与「少判一项好过删掉真实结论」相矛盾。
    [Fact]
    public void Resume_MissingNmstsRole_StillReportsCycleViolation()
    {
        // 恢复后 20ms 快发（周期违规），且 profile 未声明 NmSts 角色。
        var frames = Frames([0.0, 0.5, 1.0], [5.0, 5.02, 5.04, 5.06, 5.08]);

        var result = EvaluateWithNmsts(frames, expectNmsts: 1, declareNmstsRole: false);

        // 周期违规必须照报，不能被「缺角色」吞掉。
        var f = Assert.Single(result.Findings);
        Assert.Equal("NM_NORMAL_RESUME_SHAPE_MISMATCH", f.Code);
        // 目标状态确实没判成，必须同时显式可见。
        Assert.NotNull(result.UnmodeledReason);
        Assert.Contains("NmSts 位角色", result.UnmodeledReason, StringComparison.Ordinal);
    }

    // 这条钉住「expect_nmsts 的判别力边界」——它是 profile 里 NM_RESUME_TO_RMS
    // 至今仍标 unobservable 的原因，也是下一个人最可能重复踩的坑。
    //
    // 本地唤醒的快发（NM_03/16）与远程唤醒的正常发送（NM_02/15）**NMSts 同为 0**，
    // 因为两者都处于 Repeat Message State。所以：
    //   NMSts 分得出 NM_10（=1），分不出 NM_02/15 与 NM_03/16。
    // 真实 trace §14.6 的恢复段是 20ms 快发（NM_03/16 合规形态），
    // 若把 NM_RESUME_TO_RMS 的 expect_nmsts 设成 0 并启用，它照样会因周期不符而误报。
    // 真正的判别器是 AWB + §7.3 的唤醒源判定，不是 NMSts。
    // 本地唤醒的合规形态（NMSts=0 的快发）在 expect_nmsts=0 下仍被周期判据拒绝。
    //
    // **本用例的证据力仅限周期判据**：周期不符时实现先记违规再 continue，
    // NMSts 根本没被读取，所以它证明不了「目标状态校验执行了」。
    // 「执行了且匹配」由 Resume_ExpectNmstsMatches_NoFinding 覆盖；
    // 「执行了但不匹配」由 Resume_ExpectNmstsMismatch_ProducesFinding 覆盖。
    //
    // 它存在的意义：防止有人把 expect_nmsts 当成唤醒源判别器，据此启用规则。
    // 真实 trace §14.6 的恢复段正是这个形态。
    [Fact]
    public void Resume_ExpectNmstsZero_StillRejectsLocalWakeupBurst()
    {
        var frames = FramesWithNmsts(
            before: [(0.0, 0), (0.5, 0), (1.0, 0)],
            after: [(5.00, 0), (5.02, 0), (5.04, 0), (5.06, 0), (5.08, 0)]);

        var result = EvaluateWithNmsts(frames, expectNmsts: 0);

        var f = Assert.Single(result.Findings);
        Assert.Equal("NM_NORMAL_RESUME_SHAPE_MISMATCH", f.Code);
        Assert.Contains("正常周期", f.Message, StringComparison.Ordinal);
    }

    // spec §9.4：宁可标 Partial 也不标 Suppressed——少判一项好过删掉真实结论。
    // 既有读不出状态位的恢复点、又有已确证的周期违规时，必须**两者都报**：
    // 整条降级成 Unmodeled 会把已经确证的违规吞掉，那是信息损失。
    [Fact]
    public void Resume_UnreadablePoint_DoesNotSwallowConfirmedCycleViolation()
    {
        // 第一次恢复：DLC 只有 3 字节（读不出 byte3 的 NMSts），但周期合规。
        // 第二次恢复：NMSts 可读、周期是 20ms 快发（确证违规）。
        var shortFrame = (double T) => new ReplayFrame(T, 0x18FFFAF4, 3,
            new byte[] { 0xF4, 0x00, 0x00 },
            PeakCan.HIL.Core.FrameFlags.None, IsExtended: true);

        var frames = new List<ReplayFrame>
        {
            FrameAt(0.0, 0x18FFFAF4, 0xF4), FrameAt(0.5, 0x18FFFAF4, 0xF4),
            FrameAt(1.0, 0x18FFFAF4, 0xF4),
            // 恢复点 1：DLC=3，NMSts 读不出；周期 500ms 合规。
            shortFrame(5.0), shortFrame(5.5), shortFrame(6.0), shortFrame(6.5), shortFrame(7.0),
            // 恢复点 2：DLC=8，NMSts 可读；20ms 快发 → 周期违规。
            FrameAt(11.0, 0x18FFFAF4, 0xF4), FrameAt(11.02, 0x18FFFAF4, 0xF4),
            FrameAt(11.04, 0x18FFFAF4, 0xF4), FrameAt(11.06, 0x18FFFAF4, 0xF4),
            FrameAt(11.08, 0x18FFFAF4, 0xF4),
        };

        var result = EvaluateWithNmsts(frames.ToArray(), expectNmsts: 0);

        // 确证的周期违规必须报出来。
        var f = Assert.Single(result.Findings);
        Assert.Equal("NM_NORMAL_RESUME_SHAPE_MISMATCH", f.Code);
        // 未判定的恢复点也必须显式可见，不能被「有 finding 了」掩盖。
        Assert.NotNull(result.UnmodeledReason);
        Assert.Contains("无法读取 NMSts", result.UnmodeledReason, StringComparison.Ordinal);
        // 原因码必须是 partial_evidence，不能是 missing_profile_data：
        // profile 声明齐全，缺的是 trace 字节。用错码会让 AI 指引用户去改 profile。
        Assert.Equal("partial_evidence", result.UnmodeledCode);
    }

    // 反向守卫：不声明 expect_nmsts 时不得凭空校验目标状态，
    // 否则现有 profile（全部不写该键）会集体开始误报。
    [Fact]
    public void Resume_NoExpectNmsts_DoesNotCheckTargetState()
    {
        // 恢复后 NMSts=1，但规则没要求校验 → 不得因目标状态不符而报错。
        var frames = FramesWithNmsts(
            before: [(0.0, 0), (0.5, 0), (1.0, 0)],
            after: [(5.0, 1), (5.5, 1), (6.0, 1), (6.5, 1), (7.0, 1)]);

        var result = EvaluateWithNmsts(frames, expectNmsts: null);

        Assert.Empty(result.Findings);
        Assert.Null(result.UnmodeledReason);
    }

    /// <summary>构造带 NMSts 的帧序列：(时刻, NMSts) 对。</summary>
    private static ReplayFrame[] FramesWithNmsts(
        (double T, byte Nmsts)[] before, (double T, byte Nmsts)[] after)
    {
        var all = before.Concat(after).OrderBy(x => x.T).ToArray();
        return all.Select(x => new ReplayFrame(x.T, 0x18FFFAF4, 8,
            new byte[] { 0xF4, 0x00, 0x00, x.Nmsts, 0, 0, 0, 0 },
            PeakCan.HIL.Core.FrameFlags.None, IsExtended: true)).ToArray();
    }

    private static Outcome EvaluateWithNmsts(
        ReplayFrame[] frames, int? expectNmsts, bool declareNmstsRole = true)
    {
        var profile = declareNmstsRole
            ? Profile()
            : Profile() with { StandardBitRoles = null };
        var rule = new NmRuleDef(
            "NM_RESUME_TO_NOS",
            "静默后恢复为常规操作状态",
            "wakeup",
            new NmRuleWhen("normal_resume_shape",
                MinSilenceMs: 2000, CycleMs: 500, Pct: 0.1, MinFrames: 4),
            new NmRuleExpect("equals", ExpectNmsts: expectNmsts),
            "NM_NORMAL_RESUME_SHAPE_MISMATCH")
        {
            SourceRef = "§5.3.2",
            Clauses = ["NM_10"],
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
            rule, ctx, findings, observations, out var reason, out var code);

        Assert.True(implemented, "normal_resume_shape 必须在 v1 闭集内有实现");
        return new Outcome(findings, reason, code);
    }

    private static ReplayFrame[] Frames(double[] before, double[] after)
    {
        var all = before.Concat(after).OrderBy(t => t).ToArray();
        return all.Select(t => new ReplayFrame(t, 0x18FFFAF4, 8,
            Payload, PeakCan.HIL.Core.FrameFlags.None, IsExtended: true)).ToArray();
    }

    private static StopOutcome EvaluateStopSending(ReplayFrame[] frames)
    {
        var profile = Profile();
        var rule = new NmRuleDef(
            "NM_STOP_SENDING",
            "进入 Ready Sleep State 后停止发送 NM 报文",
            "sleep",
            new NmRuleWhen("stop_sending"),
            new NmRuleExpect("observe"),
            "")
        {
            SourceRef = "§5.3.3",
            Clauses = ["NM_09", "NM_12"],
            Observability = NmObservability.Partial,
        };

        var nmFrames = frames
            .Select(f => new NmFrame(
                new NmMessageDef(f.IsExtended ? $"0x{f.Id:X8}" : $"0x{f.Id:X3}",
                    f.IsExtended, "NM", "N", 8, true,
                    TestReasons, CycleMs: 500, TimeoutMs: 2000), f))
            .ToArray();
        var episodes = NmEpisodeBuilder.Build(nmFrames, 2000, 0, 9.0);
        var ctx = new NmRuleContext(nmFrames, episodes, profile,
            NmTimingDefaults.FromProfile(profile.Timings), new NmWindow(0, 9.0, "full"));

        var findings = new List<NmFinding>();
        var observations = new List<NmObservation>();
        var implemented = NmRuleEvaluators.TryEvaluate(
            rule, ctx, findings, observations, out var reason, out _);

        Assert.True(implemented, "stop_sending 必须在 v1 闭集内有实现");
        return new StopOutcome(findings, observations, reason);
    }

    private static ReplayFrame FrameAt(double t, uint id, byte nid)
        => new(t, id, 8, new byte[] { nid, 0x00, 0x00, 0x01, 0, 0, 0, 0 },
            PeakCan.HIL.Core.FrameFlags.None, IsExtended: true);

    private static NmProfile Profile()
        => new(NmProfileSource.Dbc,
            [new NmMessageDef("0x18FFFAF4", true, "NM", "A", 8, true,
                TestReasons, CycleMs: 500, TimeoutMs: 2000)],
            new NmTimings(CycleMs: 500, TimeoutMs: 2000),
            [new NmNodeDef("A", StationAddress: 0xF4, IsNmNode: true)],
            Array.Empty<NmConfigConflict>(), Array.Empty<string>(), new NmProvenance(),
            Layout: new NmLayout(NidByte: 0, CbvByte: 1),
            Features: new NmFeatures(PnEnabled: false),
            StandardBitRoles: [new NmBitRole(3, 0, "NmSts")]);
}
