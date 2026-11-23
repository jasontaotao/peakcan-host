using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>spec §7.2：cycle_tolerance / burst_shape / nmsts_transition。</summary>
public class NmRuleEvaluatorsTimingTests
{
    private static readonly string[] TestReasons = ["test"];

    // ==== cycle_tolerance ====

    [Fact]
    public void CycleTolerance_DeviationBeyondPct_ProducesFinding()
    {
        // 12 帧、580ms 间隔，容差 ±10% = 50ms → 超差。
        var frames = Enumerable.Range(0, 12)
            .Select(i => Frame(i * 0.58, 0x500, 0xF4, 0))
            .ToArray();

        var result = Evaluate(frames, CycleRule(500, 0.1));

        var f = Assert.Single(result.Findings);
        Assert.Equal("NM_CYCLE_DEVIATION", f.Code);
    }

    [Fact]
    public void CycleTolerance_WithinPct_NoFinding()
    {
        var frames = Enumerable.Range(0, 12)
            .Select(i => Frame(i * 0.52, 0x500, 0xF4, 0))
            .ToArray();

        var result = Evaluate(frames, CycleRule(500, 0.1));

        Assert.Empty(result.Findings);
        Assert.DoesNotContain(result.Unmodeled, u => u.RuleId == "NM_CYCLE");
    }

    [Fact]
    public void CycleTolerance_SharedIdWithAlternatingNids_Suppressed()
    {
        // 同一 CAN ID 下两个 NID 交替：间隔会被放大成假偏差，必须与 M1 同口径抑制。
        var frames = Enumerable.Range(0, 12)
            .Select(i => Frame(i * 0.56, 0x500, i % 2 == 0 ? (byte)0x11 : (byte)0x22, 0))
            .ToArray();

        var result = Evaluate(frames, CycleRule(500, 0.1));

        Assert.Empty(result.Findings);
        Assert.Contains(result.Unmodeled, u =>
            u.RuleId == "NM_CYCLE" && u.Reason.Contains("共享 ID", StringComparison.Ordinal));
    }

    [Fact]
    public void CycleTolerance_ProfileTolerance_IsNotOverriddenByBaseRule()
    {
        // 15% 偏差（575ms vs 500ms）：吉利 10% 容差应报，基础 20% 容差不报。
        // 基础规则若硬编码 pct，会把吉利收紧的 10% 覆盖回 20%，同一 trace 两条路径结论相反。
        var frames = CycleFrames(intervalCount: 11, cycleMs: 575);

        var geely = EvaluateWithProfile(frames, "profiles/oem/geely-cv@2.0.nmstd.yaml");
        var autosar = EvaluateWithProfile(frames, "profiles/base/autosar-cannm@4.1.3.nmstd.yaml");

        var f = Assert.Single(geely.Findings);
        Assert.Equal("NM_CYCLE_DEVIATION", f.Code);
        Assert.Empty(autosar.Findings);
        Assert.DoesNotContain(autosar.Unmodeled, u => u.RuleId == "NM_CYCLE");
    }

    [Fact]
    public void CycleTolerance_SingleStrayInterval_NoFinding()
    {
        // spec §7.4：单个孤立超差间隔既不到 5% 占比，也不构成连续 3 个 → 不报。
        var frames = CycleFrames(intervalCount: 80, cycleMs: 100, deviatingIndexes: [20]);

        var result = Evaluate(frames, CycleRule(100, 0.1));

        Assert.Empty(result.Findings);
        Assert.DoesNotContain(result.Unmodeled, u => u.RuleId == "NM_CYCLE");
    }

    [Fact]
    public void CycleTolerance_ThreeScatteredDeviations_NoFinding()
    {
        // 3 个偏差但全孤立：占比 3/80 = 3.75% ≤ 5%，最长连续 = 1 → 不报。
        // 这条用例专门钉住「按连续数而非总数判定」——按总数 ≥3 的旧实现会误报。
        var frames = CycleFrames(intervalCount: 80, cycleMs: 100, deviatingIndexes: [20, 40, 60]);

        var result = Evaluate(frames, CycleRule(100, 0.1));

        Assert.Empty(result.Findings);
        Assert.DoesNotContain(result.Unmodeled, u => u.RuleId == "NM_CYCLE");
    }

    [Fact]
    public void CycleTolerance_ThreeConsecutiveDeviations_ProducesFinding()
    {
        // 占比虽低（3/80），但连续 3 个间隔超容差 → 满足 §7.4 的「或」条件。
        var frames = CycleFrames(intervalCount: 80, cycleMs: 100, deviatingIndexes: [20, 21, 22]);

        var result = Evaluate(frames, CycleRule(100, 0.1));

        var f = Assert.Single(result.Findings);
        Assert.Equal("NM_CYCLE_DEVIATION", f.Code);
    }

    [Fact]
    public void CycleTolerance_InsufficientSamples_GoesToUnmodeled()
    {
        // 有效间隔 < 5 时不得下周期结论，也不能静默当作通过。
        var frames = CycleFrames(intervalCount: 3, cycleMs: 100);

        var result = Evaluate(frames, CycleRule(100, 0.1));

        Assert.Empty(result.Findings);
        Assert.Contains(result.Unmodeled, u => u.RuleId == "NM_CYCLE");
    }

    [Fact]
    public void CycleTolerance_NoFrames_GoesToUnmodeled()
    {
        var result = Evaluate(Array.Empty<NmFrame>(), CycleRule(500, 0.1));

        Assert.Empty(result.Findings);
        var u = Assert.Single(result.Unmodeled);
        Assert.Contains("NM 帧", u.Reason, StringComparison.Ordinal);
    }

    // ==== burst_shape ====

    [Fact]
    public void BurstShape_Compliant_NoFinding()
    {
        var frames = BurstFrames(count: 10, intervalSec: 0.020);

        var result = Evaluate(frames, BurstRule());

        Assert.Empty(result.Findings);
        Assert.DoesNotContain(result.Unmodeled, u => u.RuleId == "NM_03");
    }

    [Fact]
    public void BurstShape_RuleOmitsParameters_UsesProfileTiming()
    {
        // 规则只声明 kind：期望帧数 / 快发周期 / 容差全部来自生效时序（8 帧 @25ms）。
        var frames = BurstFrames(count: 8, intervalSec: 0.025);
        var standard = new NmStandardTimings { ImmediateCount = 8, ImmediateCycleMs = 25 };

        var result = Evaluate(frames, BurstRuleNoParams(), standardTimings: standard);

        Assert.Empty(result.Findings);
        Assert.DoesNotContain(result.Unmodeled, u => u.RuleId == "NM_03");
    }

    [Fact]
    public void BurstShape_RuleOmitsParameters_WrongFrameCount_ProducesFinding()
    {
        // 同样只声明 kind：6 帧不足 profile 的 8 帧，必须判不合规。
        var frames = BurstFrames(count: 6, intervalSec: 0.025);
        var standard = new NmStandardTimings { ImmediateCount = 8, ImmediateCycleMs = 25 };

        var result = Evaluate(frames, BurstRuleNoParams(), standardTimings: standard);

        Assert.Single(result.Findings);
    }

    [Fact]
    public void BurstShape_WrongFrameCount_ProducesFinding()
    {
        var frames = BurstFrames(count: 6, intervalSec: 0.020);

        var result = Evaluate(frames, BurstRule());

        Assert.Single(result.Findings);
    }

    [Fact]
    public void BurstShape_TruncatedBurst_GoesToUnmodeled()
    {
        // 段尾正好落在窗口边界：段可能被切断，时长只是下限，禁止判不合规。
        var frames = BurstFrames(count: 6, intervalSec: 0.020);

        var result = Evaluate(frames, BurstRule(), windowEnd: 0.1);

        Assert.Empty(result.Findings);
        Assert.Contains(result.Unmodeled, u => u.RuleId == "NM_03");
    }

    [Fact]
    public void BurstShape_FallbackDetector_GoesToUnmodeled()
    {
        // 无 ImmediateCycleMs 时检测器走 cycle_ratio_fallback，证明不了帧数语义，不能判形态。
        var frames = BurstFrames(count: 10, intervalSec: 0.020);

        var result = Evaluate(
            frames, BurstRule(intervalMs: null),
            timings: new NmTimings(CycleMs: 500, TimeoutMs: 2000, ImmediateCycleMs: 0));

        Assert.Empty(result.Findings);
        Assert.Contains(result.Unmodeled, u => u.RuleId == "NM_03");
    }

    [Fact]
    public void BurstShape_EpisodeEndBoundary_GoesToUnmodeled()
    {
        // 生产路径：有 episode 时逐 episode 判。段尾落在 episode 末尾，形态可能被切断。
        var frames = BurstFrames(count: 10, intervalSec: 0.020);
        var end = frames[^1].Frame.Timestamp;
        var episodes = new[]
        {
            new NmEpisode(0, frames[0].Frame.Timestamp, end, "active", null,
                Array.Empty<string>(), false, false),
        };

        var result = Evaluate(frames, BurstRule(), episodes: episodes);

        Assert.Empty(result.Findings);
        Assert.Contains(result.Unmodeled, u =>
            u.RuleId == "NM_03" && u.Reason.Contains("触到分析边界", StringComparison.Ordinal));
    }

    [Fact]
    public void BurstShape_TwoEpisodes_DoNotMergeIntoOneBurst()
    {
        // 两个 episode 各 5 帧（4 个间隔），中间是静默。逐 episode 判定应得 2 个不合规段；
        // 若忽略 episode 边界把两段当一条整体求最长连续段，只会得到 1 个（最长仍是 5 帧）。
        var frames = new[]
        {
            Frame(0.00, 0x500, 0xF4, 0), Frame(0.02, 0x500, 0xF4, 0),
            Frame(0.04, 0x500, 0xF4, 0), Frame(0.06, 0x500, 0xF4, 0),
            Frame(0.08, 0x500, 0xF4, 0),
            Frame(1.00, 0x500, 0xF4, 0), Frame(1.02, 0x500, 0xF4, 0),
            Frame(1.04, 0x500, 0xF4, 0), Frame(1.06, 0x500, 0xF4, 0),
            Frame(1.08, 0x500, 0xF4, 0),
        };
        var episodes = new[]
        {
            new NmEpisode(0, 0.00, 0.09, "active", null, Array.Empty<string>(), false, false),
            new NmEpisode(1, 1.00, 1.09, "active", null, Array.Empty<string>(), false, false),
        };

        var result = Evaluate(frames, BurstRule(), episodes: episodes);

        Assert.Equal(2, result.Findings.Count);
        Assert.DoesNotContain(result.Unmodeled, u => u.RuleId == "NM_03");
    }

    [Fact]
    public void BurstShape_NoCandidateAtAll_GoesToUnmodeled()
    {
        // 全窗口没有任何连续短间隔：规则没有实际断言形态，不能静默当作通过。
        var frames = new[]
        {
            Frame(0.0, 0x500, 0xF4, 0), Frame(1.0, 0x500, 0xF4, 0),
            Frame(2.0, 0x500, 0xF4, 0), Frame(3.0, 0x500, 0xF4, 0),
        };

        var result = Evaluate(frames, BurstRule());

        Assert.Empty(result.Findings);
        Assert.Contains(result.Unmodeled, u =>
            u.RuleId == "NM_03" && u.Reason.Contains("未观察到快发段", StringComparison.Ordinal));
    }

    [Fact]
    public void BurstShape_FramesOutsideAllEpisodes_GoesToUnmodeled()
    {
        // 帧全部落在所有 episode 之外：没有可判定的段，必须显式给出原因。
        var frames = BurstFrames(count: 6, intervalSec: 0.020);
        var episodes = new[]
        {
            new NmEpisode(0, 5.0, 6.0, "active", null, Array.Empty<string>(), false, false),
        };

        var result = Evaluate(frames, BurstRule(), episodes: episodes);

        Assert.Empty(result.Findings);
        Assert.Contains(result.Unmodeled, u =>
            u.RuleId == "NM_03" && u.Reason.Contains("episode 之外", StringComparison.Ordinal));
    }

    // ==== nmsts_transition ====

    [Fact]
    public void NmstsTransition_ProducesObservationOnly()
    {
        // 0→1 跃迁在 2.0s。规则只产 observation，绝不产 finding。
        var frames = new[]
        {
            Frame(0.0, 0x500, 0xF4, 0x00), Frame(1.0, 0x500, 0xF4, 0x00),
            Frame(2.0, 0x500, 0xF4, 0x01), Frame(3.0, 0x500, 0xF4, 0x01),
        };

        var result = Evaluate(frames, NmstsRule(), roles: [new NmBitRole(3, 0, "NmSts")]);

        Assert.Empty(result.Findings);
        Assert.DoesNotContain(result.Unmodeled, u => u.RuleId == "NM_06");
        var obs = Assert.Single(result.Observations, o => o.Kind == "nmsts_transition");
        Assert.Equal(2.0, obs.Start);
    }

    [Fact]
    public void NmstsTransition_NonObserveExpect_GoesToUnmodeled()
    {
        // 原语只实现 observe：配了 duration_within_ms 也不能静默跳过期望。
        var frames = new[]
        {
            Frame(0.0, 0x500, 0xF4, 0x00), Frame(1.0, 0x500, 0xF4, 0x01),
        };
        var rule = new NmRuleDef("NM_06", "NMSts 状态段", "wakeup",
            new NmRuleWhen("nmsts_transition", Byte: 3, Bit: 0),
            new NmRuleExpect("duration_within_ms"), "")
        {
            Clauses = ["§5.5"],
        };

        var result = Evaluate(frames, rule, roles: [new NmBitRole(3, 0, "NmSts")]);

        Assert.Empty(result.Findings);
        Assert.DoesNotContain(result.Observations, o => o.Kind == "nmsts_transition");
        var u = Assert.Single(result.Unmodeled);
        Assert.Contains("expect.kind", u.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void NmstsTransition_SharedId_Suppressed()
    {
        // 同一 CAN ID 下两个 NID 交替：切段会切碎，必须抑制。
        var frames = new[]
        {
            Frame(0.0, 0x500, 0x11, 0x00), Frame(1.0, 0x500, 0x22, 0x01),
            Frame(2.0, 0x500, 0x11, 0x00), Frame(3.0, 0x500, 0x22, 0x01),
        };

        var result = Evaluate(frames, NmstsRule(), roles: [new NmBitRole(3, 0, "NmSts")]);

        Assert.DoesNotContain(result.Observations, o => o.Kind == "nmsts_transition");
    }

    [Fact]
    public void NmstsTransition_MissingNidByte_GoesToUnmodeled()
    {
        // 未声明 NID 字节就无法判断是否共享 ID——不能假装观察成功。
        var frames = new[]
        {
            Frame(0.0, 0x500, 0xF4, 0x00), Frame(1.0, 0x500, 0xF4, 0x01),
        };

        var result = Evaluate(
            frames, NmstsRule(), roles: [new NmBitRole(3, 0, "NmSts")], declareLayout: false);

        Assert.DoesNotContain(result.Observations, o => o.Kind == "nmsts_transition");
        var u = Assert.Single(result.Unmodeled);
        Assert.Contains("nid_byte", u.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void NmstsTransition_NoRole_GoesToUnmodeled()
    {
        // profile 没声明该位：无法确认它就是 NMSts，规则必须显式不可求值。
        var frames = new[]
        {
            Frame(0.0, 0x500, 0xF4, 0x00), Frame(1.0, 0x500, 0xF4, 0x01),
        };

        var result = Evaluate(frames, NmstsRule(), roles: Array.Empty<NmBitRole>());

        Assert.DoesNotContain(result.Observations, o => o.Kind == "nmsts_transition");
        var u = Assert.Single(result.Unmodeled);
        Assert.Contains("byte3.bit0", u.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void NmstsTransition_MissingByteBit_GoesToUnmodeled()
    {
        var frames = new[]
        {
            Frame(0.0, 0x500, 0xF4, 0x00), Frame(1.0, 0x500, 0xF4, 0x01),
        };
        var rule = new NmRuleDef("NM_06", "NMSts 状态段", "wakeup",
            new NmRuleWhen("nmsts_transition"),
            new NmRuleExpect("observe"), "")
        {
            Clauses = ["§5.5"],
        };

        var result = Evaluate(frames, rule, roles: [new NmBitRole(3, 0, "NmSts")]);

        var u = Assert.Single(result.Unmodeled);
        Assert.Contains("when.byte", u.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void NmstsTransition_AllFramesTooShort_EmitsUnreadableObservation()
    {
        // 状态位所在字节越界：逐帧不可读必须显式可见，不能静默跳过后当作无跃迁。
        var frames = new[]
        {
            new NmFrame(Def("0x500"),
                new ReplayFrame(0.0, 0x500, 3, new byte[] { 0xF4, 0x00, 0x00 },
                    PeakCan.HIL.Core.FrameFlags.None)),
            new NmFrame(Def("0x500"),
                new ReplayFrame(1.0, 0x500, 3, new byte[] { 0xF4, 0x00, 0x00 },
                    PeakCan.HIL.Core.FrameFlags.None)),
        };

        var result = Evaluate(frames, NmstsRule(), roles: [new NmBitRole(3, 0, "NmSts")]);

        Assert.DoesNotContain(result.Observations, o => o.Kind == "nmsts_transition");
        Assert.Contains(result.Observations, o => o.Kind == "nmsts_transition_unreadable");
        Assert.Contains(result.Unmodeled, u => u.RuleId == "NM_06");
    }

    [Fact]
    public void NmstsTransition_CrossEpisodeGap_DoesNotInventTransition()
    {
        // 前一 episode 尾帧 NMSts=0，静默后新 episode 首帧 NMSts=1：
        // 没有观测到真正的跃迁，不能凭空造一个跨 episode 的锚点（spec §7.5.1）。
        var frames = new[]
        {
            Frame(0.0, 0x500, 0xF4, 0x00), Frame(1.0, 0x500, 0xF4, 0x00),
            Frame(5.0, 0x500, 0xF4, 0x01), Frame(6.0, 0x500, 0xF4, 0x01),
        };
        var episodes = new[]
        {
            new NmEpisode(0, 0.0, 1.0, "active", null, Array.Empty<string>(), false, false),
            new NmEpisode(1, 5.0, 6.0, "active", null, Array.Empty<string>(), false, false),
        };

        var result = Evaluate(
            frames, NmstsRule(), roles: [new NmBitRole(3, 0, "NmSts")], episodes: episodes);

        Assert.Empty(result.Findings);
        Assert.DoesNotContain(result.Unmodeled, u => u.RuleId == "NM_06");
        Assert.DoesNotContain(result.Observations, o => o.Kind == "nmsts_transition");
    }

    // ==== 构造辅助 ====

    private static NmRuleDef CycleRule(int cycleMs, double pct)
        => new("NM_CYCLE", "周期偏差", "normal",
            new NmRuleWhen("cycle_tolerance", CycleMs: cycleMs, Pct: pct),
            new NmRuleExpect("within_pct"), "NM_CYCLE_DEVIATION")
        {
            Clauses = ["§5.5"],
        };

    private static NmRuleDef BurstRule(int? intervalMs = 20)
        => new("NM_03", "快发形态", "wakeup",
            new NmRuleWhen("burst_shape", CycleMs: 500),
            new NmRuleExpect("count_equals", Count: 10, IntervalMs: intervalMs, Pct: 0.1),
            "NM_WAKEUP_BURST_MISMATCH")
        {
            Clauses = ["§5.5"],
        };

    private static NmRuleDef BurstRuleNoParams()
        // 只声明 kind：count / interval_ms / pct 全部依赖 profile 生效时序。
        => new("NM_03", "快发形态", "wakeup",
            new NmRuleWhen("burst_shape"),
            new NmRuleExpect("count_equals"),
            "NM_WAKEUP_BURST_MISMATCH")
        {
            Clauses = ["§5.5"],
        };

    private static NmRuleDef NmstsRule()
        => new("NM_06", "NMSts 状态段", "wakeup",
            new NmRuleWhen("nmsts_transition", Byte: 3, Bit: 0),
            new NmRuleExpect("observe"), "")
        {
            Clauses = ["§5.5"],
        };

    /// <summary>构造等间隔周期序列，指定下标处的间隔额外 +40ms 作为超差样本。</summary>
    private static NmFrame[] CycleFrames(int intervalCount, int cycleMs, params int[] deviatingIndexes)
    {
        var deviating = new HashSet<int>(deviatingIndexes);
        var times = new List<double> { 0 };
        var t = 0.0;
        for (var i = 0; i < intervalCount; i++)
        {
            t += (deviating.Contains(i) ? cycleMs + 40 : cycleMs) / 1000.0;
            times.Add(t);
        }
        return times.Select(x => Frame(x, 0x500, 0xF4, 0)).ToArray();
    }

    private static NmFrame[] BurstFrames(int count, double intervalSec)
        => Enumerable.Range(0, count)
            .Select(i => Frame(i * intervalSec, 0x500, 0xF4, 0))
            .ToArray();

    private static NmRuleResult Evaluate(
        IReadOnlyList<NmFrame> frames,
        NmRuleDef rule,
        IReadOnlyList<NmBitRole>? roles = null,
        NmTimings? timings = null,
        bool declareLayout = true,
        double windowEnd = 10,
        IReadOnlyList<NmEpisode>? episodes = null,
        NmStandardTimings? standardTimings = null)
    {
        var defs = frames.Select(f => f.Def).Distinct().ToArray();
        var profile = new NmProfile(
            NmProfileSource.Dbc, defs, timings ?? new NmTimings(CycleMs: 500, TimeoutMs: 2000),
            Array.Empty<NmNodeDef>(), Array.Empty<NmConfigConflict>(),
            Array.Empty<string>(), new NmProvenance(),
            Layout: declareLayout ? new NmLayout(NidByte: 0, CbvByte: 1) : null,
            StandardBitRoles: roles);
        var timing = NmTimingDefaults.FromProfile(profile.Timings, standardTimings);
        var ctx = new NmRuleContext(frames, episodes ?? Array.Empty<NmEpisode>(), profile, timing,
            new NmWindow(0, windowEnd, "test")) { Rules = new[] { rule } };
        return NmRuleEngine.Evaluate(ctx.Rules, ctx);
    }

    /// <summary>
    /// 用仓库真实的 profile 文件求值：先加载 base，再按需叠加 OEM 并通过
    /// <see cref="NmStandardProfileBinding"/> 绑定到项目 profile（与生产路径一致）。
    /// </summary>
    private static NmRuleResult EvaluateWithProfile(
        IReadOnlyList<NmFrame> frames, string profileRelativePath)
    {
        var baseProfile = NmStandardYaml.LoadFile(
            RepoPath("profiles/base/autosar-cannm@4.1.3.nmstd.yaml"));
        var registry = new Dictionary<string, NmStandardProfile>(StringComparer.Ordinal)
        {
            [$"{baseProfile.StandardId}@{baseProfile.Version}"] = baseProfile,
        };

        var yaml = File.ReadAllText(RepoPath(profileRelativePath));
        var selected = NmStandardYaml.Load(yaml);
        registry[$"{selected.StandardId}@{selected.Version}"] = selected;
        var flat = NmStandardYaml.LoadAndFlatten(yaml, registry);

        var defs = frames.Select(f => f.Def).Distinct().ToArray();
        var project = new NmProfile(
            NmProfileSource.Dbc, defs, new NmTimings(),
            Array.Empty<NmNodeDef>(), Array.Empty<NmConfigConflict>(),
            Array.Empty<string>(), new NmProvenance(),
            Layout: new NmLayout(NidByte: 0, CbvByte: 1));
        var bound = NmStandardProfileBinding.Bind(project, flat);
        var timing = NmTimingDefaults.FromProfile(bound.Timings, bound.StandardTimings);
        var rule = FindRule(flat.Rules!, "NM_CYCLE");

        var ctx = new NmRuleContext(frames, Array.Empty<NmEpisode>(), bound, timing,
            new NmWindow(0, 10, "test")) { Rules = new[] { rule } };
        return NmRuleEngine.Evaluate(ctx.Rules, ctx);
    }

    private static NmRuleDef FindRule(IReadOnlyList<NmRuleDef> rules, string id)
    {
        for (var i = 0; i < rules.Count; i++)
        {
            if (string.Equals(rules[i].Id, id, StringComparison.Ordinal)) return rules[i];
        }
        throw new InvalidOperationException($"profile 缺少规则 {id}");
    }

    private static string RepoPath(string relative)
    {
        // 本测试项目有同名 namespace PeakCan.Host.Core.Tests.Path，显式指回 System.IO.Path。
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
               && !File.Exists(System.IO.Path.Combine(dir.FullName, "PeakCan.Host.slnx")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return System.IO.Path.Combine(dir!.FullName, relative);
    }

    private static NmMessageDef Def(string id, bool isExtended = false)
        => new(id, isExtended, "NM", "A", 8, true, TestReasons, CycleMs: 500, TimeoutMs: 2000);

    private static NmFrame Frame(double t, uint id, byte b0, byte b1)
        // b1 同时写 byte1 和 byte3：nmsts_transition 读 byte3，bit_rule 读 byte1。
        => new(Def($"0x{id:X}"),
            new ReplayFrame(t, id, 8, new byte[] { b0, b1, 0, b1, 0, 0, 0, 0 },
                PeakCan.HIL.Core.FrameFlags.None));
}
