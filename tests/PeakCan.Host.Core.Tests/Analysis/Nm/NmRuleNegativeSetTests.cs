using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// spec §12 合法场景负例集。这些场景在真实网络上完全正常，
/// 任何一条产出 <c>warn</c> 级 finding 都是误报，必须挡住。
/// <para>
/// info 级 finding（如 <c>NM_ALL_NM_SILENT</c>）是可复核的事实，不是违规：
/// 合法释放后全网静默本就是正确现象，断言的是「没有 warn」，不是「报告为空」。
/// </para>
/// </summary>
public class NmRuleNegativeSetTests
{
    private static readonly string[] TestReasons = ["test"];

    [Fact]
    public void LegalPassiveWakeup_NoViolation()
    {
        // 被动唤醒：AWB=0，无快发。RMS 段 NMSts=0 从 0.1s 持续到 1.6s = 1500ms
        // （在吉利 1440–1760ms 内），之后 NMSts=1 进入 NOS，稳态 500ms 周期。
        // 截断形态：RMS 后面还有 NOS 帧，段尾未触到 episode 末帧 → 不截断。
        var frames = new[]
        {
            Frame(0.1, awb: 0, nmsts: 0), Frame(0.6, awb: 0, nmsts: 0),
            Frame(1.1, awb: 0, nmsts: 0), Frame(1.6, awb: 0, nmsts: 0),
            Frame(2.1, awb: 0, nmsts: 1), Frame(2.6, awb: 0, nmsts: 1),
            Frame(3.1, awb: 0, nmsts: 1), Frame(3.6, awb: 0, nmsts: 1),
        };

        var result = Evaluate(frames, windowEnd: 6.0);

        AssertNoViolation(result);

        // 非空洞性守卫：证明规则引擎真的执行了，而不是全部落进 Unmodeled。
        // NM_06 必须产出 RMS 观测（1500ms 是可判定的实测值）。
        var rms = Assert.Single(result.Rules.Observations,
            o => o.Kind == "repeat_message_duration" && o.RuleId == "NM_06");
        Assert.Contains("1500ms", rms.Detail);
        // NM_01 / NM_05 已实际求值并通过，不能以「未建模」冒充通过。
        Assert.DoesNotContain(result.Rules.Unmodeled, u => u.Clause == "NM_01");
        Assert.DoesNotContain(result.Rules.Unmodeled, u => u.Clause == "NM_05");
    }

    [Fact]
    public void LegalActiveWakeup_NoViolation()
    {
        // 主动唤醒：AWB=1。10 帧快发（20ms 间隔，恰为 N_ImmediateNM_TIMES=10），
        // 随后 500ms 稳态，NMSts 0→1 进入 NOS。
        // 截断形态：快发段尾 0.28s 远早于 episode 末帧 2.9s → 不截断，可判合规。
        var burst = Enumerable.Range(0, 10)
            .Select(i => Frame(0.1 + i * 0.020, awb: 1, nmsts: 0));
        var steady = Enumerable.Range(0, 6)
            .Select(i => Frame(0.4 + i * 0.5, awb: 1, nmsts: (byte)(i < 2 ? 0 : 1)));
        var frames = burst.Concat(steady).ToArray();

        var result = Evaluate(frames, windowEnd: 5.0);

        AssertNoViolation(result);

        // 非空洞性守卫：burst_shape 必须真的判过一次。
        // 10 帧 / 20ms 被判合规时不会留下 finding，也不应退化成「无候选 → Unmodeled」。
        Assert.DoesNotContain(result.Rules.Unmodeled, u => u.Clause == "NM_03");
    }

    [Fact]
    public void LegalReleaseToSleep_NoViolation()
    {
        // 正常释放：RMS 1.5s（0.0→1.5，NMSts=0），2.0s 后进入 NOS 并停发，
        // 窗口到 6.0s。全网静默 4.0s 是**正确事实**，只允许 info 级
        // NM_ALL_NM_SILENT，不允许任何 warn。
        // 截断形态：帧在 2.0s 停止、窗口继续到 6.0s；episode 末帧即 2.0s，
        // 且窗口结束距末帧 4.0s ≥ 超时 → episode 自然结束，非截断。
        var frames = new[]
        {
            Frame(0.0, awb: 0, nmsts: 0), Frame(0.5, awb: 0, nmsts: 0),
            Frame(1.0, awb: 0, nmsts: 0), Frame(1.5, awb: 0, nmsts: 0),
            Frame(2.0, awb: 0, nmsts: 1),
        };

        var result = Evaluate(frames, windowEnd: 6.0);

        AssertNoViolation(result);

        // 断言静默事实确实出现（比「只是容忍」更强）：合法释放后全网静默，
        // 但它是 info，不是违规。
        var silence = Assert.Single(result.Gap.Findings,
            f => f.Code == "NM_ALL_NM_SILENT" && f.Severity == "info");
        Assert.Contains("4.00s", silence.Message);
    }

    [Fact]
    public void RssStopSending_NoViolation()
    {
        // NM_09：进入 Ready Sleep State（NMSts=1）后停止发送是合规的，不能判违规。
        // 规则集刻意不含 NM_05（RMR 预留位），让本场景只关注「停发」语义。
        // 截断形态：末帧 1.5s、窗口 5.0s，尾部静默 3.5s ≥ 超时 → episode 自然结束。
        var frames = new[]
        {
            Frame(0.0, awb: 0, nmsts: 0), Frame(0.5, awb: 0, nmsts: 0),
            Frame(1.0, awb: 0, nmsts: 0), Frame(1.5, awb: 0, nmsts: 1),
        };
        var rules = Rules().Where(r => r.Id != "NM_05").ToArray();

        var result = Evaluate(frames, windowEnd: 5.0, rules: rules);

        AssertNoViolation(result);
    }

    [Fact]
    public void TruncatedWindow_NoViolation()
    {
        // 窗口在 RMS 中间结束：6 帧全 NMSts=0，跨度 2.5s；若未被截断，NM_06 本会
        // 判 2500ms > 1760ms 超差——正是要验的抑制路径。
        // 截断形态：窗口末帧就是 episode 末帧（2.5s），且 BoundaryEnd=true，
        // 时长只能当下限，规则必须只产 boundary 观测、不产 finding。
        var frames = Steady(0.0, count: 6, stepSec: 0.5, awb: 0, nmsts: 0);

        // 窗口正好落在末帧上 → 段尾触到窗口边界。
        var result = Evaluate(frames, windowEnd: frames[^1].Frame.Timestamp);

        AssertNoViolation(result);

        // 非空洞性守卫：截断抑制路径确实被执行（观测标记 boundary + 文案说明下限），
        // 而不是因为规则没跑才没有 finding。
        var rms = Assert.Single(result.Rules.Observations,
            o => o.Kind == "repeat_message_duration" && o.RuleId == "NM_06");
        Assert.Equal("boundary", rms.EvidenceKind);
        Assert.Contains("窗口截断", rms.Detail);
    }

    /// <summary>
    /// warn 级 finding 是违规；info 级（NM_ALL_NM_SILENT 等）是可复核事实，允许出现。
    /// 规则引擎与空档归因两条产出路径都必须零 warn。
    /// </summary>
    private static void AssertNoViolation(Evaluation result)
    {
        Assert.DoesNotContain(result.Rules.Findings, f => f.Severity == "warn");
        Assert.DoesNotContain(result.Gap.Findings, f => f.Severity == "warn");
    }

    /// <summary>规则引擎 + 空档归因的联合结果（两者都可能产出 finding）。</summary>
    private sealed record Evaluation(NmRuleResult Rules, NmGapResult Gap);

    private static Evaluation Evaluate(
        IReadOnlyList<NmFrame> frames, double windowEnd,
        IReadOnlyList<NmRuleDef>? rules = null)
    {
        var defs = frames.Select(f => f.Def).Distinct().ToArray();
        var profile = new NmProfile(
            NmProfileSource.Dbc, defs, new NmTimings(CycleMs: 500, TimeoutMs: 2000),
            new[] { new NmNodeDef("A", 0xF4, null, true) },
            Array.Empty<NmConfigConflict>(), Array.Empty<string>(), new NmProvenance(),
            Layout: new NmLayout(NidByte: 0, CbvByte: 1),
            StandardBitRoles: new[]
            {
                new NmBitRole(1, 0, "RepeatMessageRequest")
                    { Constraint = NmBitConstraint.MustBeZeroWhileDisabled },
                new NmBitRole(1, 4, "ActiveWakeup"),
                new NmBitRole(3, 0, "NmSts"),
            },
            StandardRules: rules ?? Rules());

        var timing = NmTimingDefaults.FromProfile(profile.Timings, null);
        var episodes = NmEpisodeBuilder.Build(frames, timing.TimeoutMs, 0, windowEnd);
        var window = new NmWindow(0, windowEnd, "test");
        var ctx = new NmRuleContext(frames, episodes, profile, timing, window)
        { Rules = profile.StandardRules! };
        var engine = NmRuleEngine.Evaluate(ctx.Rules, ctx);
        var gap = NmGapAnalyzer.Analyze(new NmGapInput(
            "test",
            frames,
            frames.Select(f => f.Frame).ToArray(),
            profile,
            episodes,
            window,
            new NmSelectionDiagnostics(0, 0, 0, 0, 0, 0, false, false),
            timing));
        return new Evaluation(engine, gap);
    }

    private static NmRuleDef[] Rules() => new[]
    {
        new NmRuleDef("NM_01", "NID 与 ID 映射一致", "normal",
            new NmRuleWhen("nid_id_mapping"),
            new NmRuleExpect("matches_id_low_byte"), "NM_NID_MISMATCH"),
        new NmRuleDef("NM_05", "预留位必须为 0", "normal",
            new NmRuleWhen("bit_rule", Role: "RepeatMessageRequest"),
            new NmRuleExpect("is_zero"), "NM_RMR_SET_WHILE_DISABLED"),
        new NmRuleDef("NM_06", "RMS 持续时长", "wakeup",
            new NmRuleWhen("repeat_message_duration", CycleMs: 500),
            new NmRuleExpect("duration_within_ms", MinMs: 1440, MaxMs: 1760,
                Anchor: "nmsts"), "NM_REPEAT_MESSAGE_DURATION_EXCEEDED")
        {
            Observability = NmObservability.Partial,
        },
        new NmRuleDef("NM_03", "快发形态", "wakeup",
            new NmRuleWhen("burst_shape", CycleMs: 500),
            new NmRuleExpect("count_equals", Count: 10, IntervalMs: 20, Pct: 0.1),
            "NM_WAKEUP_BURST_MISMATCH")
        {
            Observability = NmObservability.Partial,
        },
    };

    private static NmFrame[] Steady(double start, int count, double stepSec, byte awb, byte nmsts)
        => Enumerable.Range(0, count)
            .Select(i => Frame(start + i * stepSec, awb, nmsts))
            .ToArray();

    /// <summary>
    /// 标准帧 0x500 的期望 NID = (0x500 - 0x400) &amp; 0xFF = 0x00，故 byte0 必须写 0x00，
    /// 否则 NM_01 会正确报 NM_NID_MISMATCH。NMSts 在 byte3.bit0，ActiveWakeup 在 byte1.bit4。
    /// </summary>
    private static NmFrame Frame(double t, byte awb, byte nmsts)
        => new(new NmMessageDef("0x500", false, "NM", "A", 8, true,
                TestReasons, CycleMs: 500, TimeoutMs: 2000),
            new ReplayFrame(t, 0x500, 8,
                new byte[] { 0x00, (byte)(awb << 4), 0, nmsts, 0, 0, 0, 0 },
                PeakCan.HIL.Core.FrameFlags.None));
}
