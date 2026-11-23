using System.Globalization;
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
/// <para>
/// 全部场景必须走生产入口 <see cref="NmAnalyzer.Analyze"/>：内容分析（NID/站地址）、
/// 空档归因与规则引擎都在门面内串联。直接调引擎/空档归因会绕开内容分析，
/// 而内容分析恰恰是最容易在合法报文上误报的那条路径。
/// </para>
/// <para>
/// 文件末尾的 <see cref="LastEpisodeRmsFollowedByNos_StillReportsOverage"/> 不是负例，
/// 而是 Task 8 C1（末段 RMS 被整段抑制）的回归守卫：它必须产出 finding。
/// </para>
/// </summary>
public class NmRuleNegativeSetTests
{
    private static readonly string[] TestReasons = ["test"];

    // 节点 A 的标准帧 0x5F4 → NID = (0x5F4 - 0x400) & 0xFF = 0xF4，与站地址 0xF4 一致。
    // 原 fixture 用 0x500 + 站地址 0xF4（NID 0x00），走生产入口时 NmContentAnalyzer
    // 会正确报 NM_NID_MISMATCH——那是 fixture 自相矛盾，不是引擎误报。
    private const string CanIdA = "0x5F4";
    private const string CanIdB = "0x5F5";

    // 主动唤醒场景的时间点常量：CA1861 要求复用静态数组而不是内联 new[]。
    private static readonly double[] WakeupSteadyZeroTimes = [0.40, 0.88, 1.38, 1.84];
    private static readonly double[] WakeupSteadyOneTimes = [2.34, 2.84];

    [Fact]
    public void LegalPassiveWakeup_NoViolation()
    {
        // 被动唤醒：AWB=0，无快发。RMS 段 NMSts=0 从 0.1s 持续到 1.6s = 1500ms
        // （在 1440–1760ms 内），之后 NMSts=1 进入 NOS，稳态 500ms 周期。
        // 截断形态：RMS 后面还有 NOS 帧，段尾未触到 episode 末帧 → 不截断。
        var frames = new[]
        {
            Frame(0.1, awb: 0, nmsts: 0), Frame(0.6, awb: 0, nmsts: 0),
            Frame(1.1, awb: 0, nmsts: 0), Frame(1.6, awb: 0, nmsts: 0),
            Frame(2.1, awb: 0, nmsts: 1), Frame(2.6, awb: 0, nmsts: 1),
            Frame(3.1, awb: 0, nmsts: 1), Frame(3.6, awb: 0, nmsts: 1),
        };

        var report = Evaluate(frames, windowEnd: 6.0);

        AssertNoViolation(report);

        // 非空洞性守卫：证明规则引擎真的执行了，而不是全部落进 Unmodeled。
        // NM_06 必须产出 RMS 观测；时长用数值断言，避免依赖 F0 格式化的区域设置。
        var rms = Assert.Single(ObservationsOf(report),
            o => o.Kind == "repeat_message_duration" && o.RuleId == "NM_06");
        Assert.Equal(1500.0, (rms.End - rms.Start) * 1000.0, 6);
        // NM_01 / NM_05 已实际求值并通过，不能以「未建模」冒充通过。
        Assert.DoesNotContain(UnmodeledOf(report), u => u.Clause == "NM_01");
        Assert.DoesNotContain(UnmodeledOf(report), u => u.Clause == "NM_05");
    }

    [Fact]
    public void LegalActiveWakeup_NoViolation()
    {
        // 主动唤醒：AWB=1。10 帧快发（20ms 间隔，恰为 N_ImmediateNM_TIMES=10），
        // 随后 500ms 稳态，NMSts 0→1 进入 NOS。
        // RMS 从快发首帧 0.1s 到最后一个 NMSts=0 帧 1.84s = 1740ms，落在 1440–1760ms 内
        // （原 fixture 的 800ms 低于下限，只因原语只判上限才「合法」，与规则自相矛盾）。
        // 截断形态：快发段尾 0.28s 远早于 episode 末帧 2.84s → 不截断，可判合规。
        var burst = Enumerable.Range(0, 10)
            .Select(i => Frame(0.1 + i * 0.020, awb: 1, nmsts: 0));
        var steady = WakeupSteadyZeroTimes
            .Select(t => Frame(t, awb: 1, nmsts: 0))
            .Concat(WakeupSteadyOneTimes
                .Select(t => Frame(t, awb: 1, nmsts: 1)));
        var frames = burst.Concat(steady).ToArray();

        var report = Evaluate(frames, windowEnd: 5.0);

        AssertNoViolation(report);

        // 非空洞性守卫：burst_shape 必须真的判过一次合规。
        // 10 帧 / 20ms 被判合规时不会留下 finding，也不应退化成「无候选 → Unmodeled」。
        Assert.DoesNotContain(UnmodeledOf(report), u => u.Clause == "NM_03");
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

        var report = Evaluate(frames, windowEnd: 6.0);

        AssertNoViolation(report);

        // 断言静默事实确实出现（比「只是容忍」更强）：合法释放后全网静默，
        // 但它是 info，不是违规。时长断言取证据时刻，不依赖 F2 的区域化文本。
        var silence = Assert.Single(report.Findings,
            f => f.Code == "NM_ALL_NM_SILENT" && f.Severity == "info");
        Assert.Equal(4.0, silence.EvidenceTimes[1] - silence.EvidenceTimes[0], 6);
    }

    [Fact]
    public void RssStopSending_NoViolation()
    {
        // NM_09：进入 Ready Sleep State（NMSts=1）后停止发送是合规的，不能判违规。
        // 第二个报文 B 在 A 停发后继续正常发送，唯一目的是让 NmGapAnalyzer 的
        // 逐报文分支（perMessage.Length > 1）真正运行——单报文场景该分支被结构性跳过，
        // 即使 fixture 从未停发也照样「通过」，证明不了任何事。
        // A 的 RMS 观测：0.0→1.5s = 1500ms。注意本场景有两个报文，而
        // repeat_message_duration 在同一个 episode 内不按报文分组（取 episode 内全部帧），
        // 交错出现的 B 帧会打断 A 的首个 NMSts=0 段，NM_06 在这里实际进 Unmodeled；
        // RMS 时长的数值断言放在单报文的 LegalPassiveWakeup_NoViolation 里。
        // 2.0s 进入 RSS 后停发。
        var a = new[]
        {
            Frame(0.0, awb: 0, nmsts: 0), Frame(0.5, awb: 0, nmsts: 0),
            Frame(1.0, awb: 0, nmsts: 0), Frame(1.5, awb: 0, nmsts: 0),
            Frame(2.0, awb: 0, nmsts: 1),
        };
        var b = Enumerable.Range(0, 10)
            .Select(i => Frame(0.25 + i * 0.5, awb: 0, nmsts: 1, CanIdB, "B"));
        // 两个报文的帧必须按时间合并且单调：乱序会触发 NM_TIMESTAMP_REGRESSION，
        // 那是 fixture 的缺陷，不是 NM_09 要验的东西。
        var frames = a.Concat(b).OrderBy(f => f.Frame.Timestamp).ToArray();

        var report = Evaluate(frames, windowEnd: 5.0);

        AssertNoViolation(report);

        // 显式断言：合法停发的节点不能被归因为掉线。
        // 该误报已由 Task 9.5 修复（NmGapAnalyzer 读取停发前末帧的 NMSts），
        // 本用例现在是绿的真实守卫；若以后回退成「只按停发判定」会重新变红。
        Assert.DoesNotContain(report.Findings, f => f.Code == "NM_NODE_DROPOUT");

        // 同一事实也要反映到逐报文 verdict：AI 读的是 SilenceVerdict，
        // 它不能与 warn 层矛盾地说成「可疑的单节点静默」。
        var stopped = Assert.Single(report.Messages, m => m.CanIdHex == CanIdA);
        Assert.NotEqual(NmSilenceVerdict.SilentWithOtherNmActive, stopped.SilenceVerdict);
        Assert.False(stopped.SilentBeyondTimeout);
    }

    [Fact]
    public void TruncatedWindow_NoViolation()
    {
        // 窗口在 RMS 中间结束：6 帧全 NMSts=0，跨度 2.5s；若未被截断，NM_06 本会
        // 判 2500ms > 1760ms 超差——正是要验的抑制路径。
        // NmFrameSelector 丢弃 timestamp >= End 的帧，故 2.6s 窗口保留 2.5s 末帧。
        // 截断形态：episode 被窗口切断（BoundaryEnd=true）且段尾就是 episode 末帧
        // （2.5s）→ 时长只能当下限，规则必须只产 boundary 观测、不产 finding。
        var frames = Steady(0.0, count: 6, stepSec: 0.5, awb: 0, nmsts: 0);
        Assert.Equal(2.5, frames[^1].Frame.Timestamp, 6);

        var report = Evaluate(frames, windowEnd: 2.6);

        AssertNoViolation(report);

        // 非空洞性守卫：截断抑制路径确实被执行（观测标记 boundary + 文案说明下限），
        // 而不是因为规则没跑才没有 finding。
        var rms = Assert.Single(ObservationsOf(report),
            o => o.Kind == "repeat_message_duration" && o.RuleId == "NM_06");
        Assert.Equal("boundary", rms.EvidenceKind);
        Assert.Contains("窗口截断", rms.Detail);
        Assert.Equal(2500.0, (rms.End - rms.Start) * 1000.0, 6);
    }

    [Fact]
    public void LegalCycleJitter_NoCycleDeviationOrDlcMismatch()
    {
        // 真实抖动：500ms 周期下逐帧 505–537ms（+1%…+7.4%），全部落在 ±10% 容差
        // （±50ms）内。cycle_tolerance 是最容易把正常抖动误报成偏差的原语，必须有
        // 合法场景负例；同时让 layout_check 真正求值（全部帧 DLC=8，与矩阵一致）。
        double[] deltas = [0.514, 0.522, 0.537, 0.505, 0.519, 0.530, 0.514, 0.511, 0.526];
        var times = new List<double> { 0.0 };
        foreach (var delta in deltas) times.Add(times[^1] + delta);
        var frames = times.Select(t => Frame(t, awb: 0, nmsts: 1)).ToArray();

        var report = Evaluate(frames, windowEnd: 6.0);

        AssertNoViolation(report);

        // 非空洞性守卫：两条原语都必须真的求值（未进入 Unmodeled）。
        Assert.DoesNotContain(UnmodeledOf(report), u => u.Clause == "NM_CYCLE_500");
        // layout_check 在 DLC 一致时不会产 finding；「不在 Unmodeled」是空断言
        // （LayoutCheck 从不设置 unmodeledReason），真正的阴性证据是 DLC 不一致的
        // finding 不存在。正向路径由 LayoutDlcMismatch_ReportsDlcMismatch 覆盖。
        Assert.DoesNotContain(report.Findings, f => f.Code == "NM_DLC_MISMATCH");

        // M1 周期统计同样必须给出「零偏差、样本充足」的实测值。
        var message = Assert.Single(report.Messages);
        Assert.True(message.Cycle.IntervalCount >= 5, "周期样本不足会掩盖偏差统计");
        Assert.Equal(0, message.Cycle.DeviationCount);
    }

    [Fact]
    public void LayoutDlcMismatch_ReportsDlcMismatch()
    {
        // 正向用例（不是负例）：实测 DLC=7 与矩阵声明 8 不一致，layout_check 必须
        // 真的产出 NM_DLC_MISMATCH。负例集里 layout_check 只有「无 finding」的阴性证据，
        // 本用例是它唯一的阳性路径，防止原语静默失效（比如判据写反）而无人发现。
        var frames = new[]
        {
            Frame(0.0, awb: 0, nmsts: 1, dlc: 7),
            Frame(0.5, awb: 0, nmsts: 1),
        };

        var report = Evaluate(frames, windowEnd: 2.0);

        // RuleId 区分规则引擎副本与 M1 的 NM_DLC_MISMATCH，确保规则原语确实执行。
        var finding = Assert.Single(report.Findings, f => f.RuleId == "NM_LAYOUT");
        Assert.Equal("NM_DLC_MISMATCH", finding.Code);
        Assert.Equal("warn", finding.Severity);
    }

    [Fact]
    public void LastEpisodeRmsFollowedByNos_StillReportsOverage()
    {
        // Task 8 C1 回归守卫（非负例）：RMS 落在**最后一个 episode**（BoundaryEnd=true），
        // 但后面还有 NOS 帧，段尾并未触到 episode 末帧（2.5s < 4.0s）。
        // 若截断判据回退成 `episode.BoundaryEnd || end >= Window.End`，整段 RMS 会被
        // 误当「下限」而永远不判超差——本用例必须产出 finding，否则 C1 回归无人拦截。
        var rms = Steady(0.0, count: 6, stepSec: 0.5, awb: 0, nmsts: 0);    // 0→2.5s
        var nos = Steady(3.0, count: 3, stepSec: 0.5, awb: 0, nmsts: 1);    // 3.0→4.0s
        var frames = rms.Concat(nos).ToArray();

        var report = Evaluate(frames, windowEnd: 4.4);

        // 前提校验：末 episode 确实被窗口边界切断，且 RMS 终点在其内部而非末帧。
        var lastEpisode = report.Episodes[^1];
        Assert.True(lastEpisode.BoundaryEnd);
        Assert.Equal(4.0, lastEpisode.End, 6);
        Assert.True(2.5 < lastEpisode.End);

        var finding = Assert.Single(report.Findings,
            f => f.Code == "NM_REPEAT_MESSAGE_DURATION_EXCEEDED");
        Assert.Equal("warn", finding.Severity);
        Assert.Equal("NM_06", finding.RuleId);
        Assert.Equal(2500.0,
            (finding.EvidenceTimes[1] - finding.EvidenceTimes[0]) * 1000.0, 6);

        // 观测不能带「窗口截断」：段尾没有触到任何边界。
        var rmsObservation = Assert.Single(ObservationsOf(report),
            o => o.Kind == "repeat_message_duration" && o.RuleId == "NM_06");
        Assert.DoesNotContain("窗口截断", rmsObservation.Detail);
        Assert.DoesNotContain(UnmodeledOf(report), u => u.Clause == "NM_06");
    }

    /// <summary>
    /// warn 级 finding 是违规；info 级（NM_ALL_NM_SILENT 等）是可复核事实，允许出现。
    /// 生产入口已把规则、内容、空档三条路径的 finding 合并进同一份报告。
    /// </summary>
    private static void AssertNoViolation(NmReport report)
    {
        var violations = report.Findings.Where(f => f.Severity == "warn").ToArray();
        Assert.True(
            violations.Length == 0,
            "合法场景不得产生 warn 级 finding：" +
            string.Join("；", violations.Select(v => $"{v.Code}（{v.Message}）")));
    }

    private static IReadOnlyList<NmObservation> ObservationsOf(NmReport report)
        => report.Observations ?? Array.Empty<NmObservation>();

    private static IReadOnlyList<NmUnmodeledRule> UnmodeledOf(NmReport report)
        => report.UnmodeledRules ?? Array.Empty<NmUnmodeledRule>();

    /// <summary>
    /// 一律走生产入口：NmAnalyzer 内部依次执行帧选择、episode 切分、逐报文周期、
    /// 空档归因、内容分析（NID/站地址等）与规则引擎。绕开它等于绕开内容分析，
    /// 而内容分析正是最可能在合法流量上误报的一环。
    /// </summary>
    private static NmReport Evaluate(
        IReadOnlyList<NmFrame> frames, double windowEnd)
    {
        var defs = frames.Select(f => f.Def).Distinct().ToArray();

        // 站地址按标准帧映射自动推导，保证与 NM_01 的 (ID-0x400)&0xFF 同源、不会自相矛盾。
        var profile = new NmProfile(
            NmProfileSource.Dbc, defs, new NmTimings(CycleMs: 500, TimeoutMs: 2000),
            NodesFor(defs),
            Array.Empty<NmConfigConflict>(), Array.Empty<string>(), new NmProvenance(),
            Layout: new NmLayout(NidByte: 0, CbvByte: 1),
            StandardBitRoles: BitRoles(),
            StandardRules: Rules());

        return NmAnalyzer.Analyze(new NmAnalysisRequest(
            "test", null, frames.Select(f => f.Frame).ToArray(), null, profile, 0, windowEnd));
    }

    private static NmNodeDef[] NodesFor(IEnumerable<NmMessageDef> defs)
        => defs.Select(d => new NmNodeDef(
            d.Sender ?? "?",
            NidFor(d.CanIdHex),
            null,
            true)).ToArray();

    /// <summary>标准帧 NID = (CAN ID - 0x400) &amp; 0xFF；夹具全部是标准帧。</summary>
    private static byte NidFor(string canIdHex)
        => (byte)((CanIdOf(canIdHex) - 0x400) & 0xFF);

    private static uint CanIdOf(string canIdHex)
        => uint.Parse(canIdHex.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static NmBitRole[] BitRoles() => new[]
    {
        new NmBitRole(1, 0, "RepeatMessageRequest")
            { Constraint = NmBitConstraint.MustBeZeroWhileDisabled },
        new NmBitRole(1, 4, "ActiveWakeup"),
        new NmBitRole(3, 0, "NmSts"),
    };

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
        // cycle_tolerance 与 layout_check 之前没有任何场景级负例；前者对正常抖动最易误报，
        // 后者此前全仓无测试。放进默认规则集，让每个合法场景顺带覆盖。
        new NmRuleDef("NM_CYCLE_500", "500ms 周期容差 ±10%", "normal",
            new NmRuleWhen("cycle_tolerance", CycleMs: 500, Pct: 0.1),
            new NmRuleExpect("within_pct"), "NM_CYCLE_DEVIATION"),
        new NmRuleDef("NM_LAYOUT", "帧长与矩阵一致", "normal",
            new NmRuleWhen("layout_check"),
            new NmRuleExpect("equals"), "NM_DLC_MISMATCH"),
    };

    private static NmFrame[] Steady(
        double start, int count, double stepSec, byte awb, byte nmsts)
        => Enumerable.Range(0, count)
            .Select(i => Frame(start + i * stepSec, awb, nmsts))
            .ToArray();

    /// <summary>
    /// 标准 NM 帧。byte0=NID（由 CAN ID 推导，保证 NM_01 与内容分析的站地址检查一致），
    /// byte1 的 bit4=ActiveWakeup、bit0 保持 0，byte3.bit0=NMSts。
    /// </summary>
    private static NmFrame Frame(
        double t, byte awb, byte nmsts,
        string canId = CanIdA, string sender = "A", int dlc = 8)
    {
        // 载荷长度与 DLC 保持一致；byte0=NID、byte1.bit4=ActiveWakeup、byte3.bit0=NMSts。
        // dlc 小于 4 时本 helper 不适用于位断言，调用方需自行保证 >=4。
        var payload = new byte[dlc];
        payload[0] = NidFor(canId);
        payload[1] = (byte)(awb << 4);
        if (dlc > 3) payload[3] = nmsts;

        return new(new NmMessageDef(canId, false, "NM", sender, 8, true,
                TestReasons, CycleMs: 500, TimeoutMs: 2000),
            new ReplayFrame(t, CanIdOf(canId), (byte)dlc, payload,
                PeakCan.HIL.Core.FrameFlags.None));
    }
}
