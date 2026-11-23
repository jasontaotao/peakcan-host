using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// spec §14.6：真实 trace 两段 RMS 实测 2.121s / 2.325s，允许窗 1.440–1.560 / 1.640–1.760。
/// 必须能检出，否则 repeat_message_duration 实现了也验不出来。
/// </summary>
public class NmRuleEvaluatorsLatencyTests
{
    private static readonly string[] TestReasons = ["test"];

    // CA1861：participants 数组在多处传给 NmEpisode，复用同一实例。
    private static readonly string[] ParticipantA = ["A"];

    // C1 回归用：两个独立节点的标准 NM 报文（NID 不同 → 不共享 ID）。
    private const uint NmIdA = 0x5F4;
    private const uint NmIdB = 0x5F5;
    private const uint NmIdPms = 0x5F4;
    private const uint NmIdBms = 0x5F5;

    // ==== repeat_message_duration ====

    [Fact]
    public void RepeatMessageDuration_NoBurst_OverLimit_ProducesFinding()
    {
        // 无快发：允许 1440–1560ms，实测 2121ms（真实 trace 第一段 §14.6）。
        var frames = Steady(0.0, count: 6, stepSec: 0.42);   // 跨 2.1s

        var result = Evaluate(frames, Rule(min: 1440, max: 1560, anchor: "nmsts"));

        var f = Assert.Single(result.Findings);
        Assert.Equal("NM_REPEAT_MESSAGE_DURATION_EXCEEDED", f.Code);
        Assert.True(f.IsInference, "P 级原语必须标推断");
    }

    [Fact]
    public void RepeatMessageDuration_BurstShapedTrace_OverLimit_ProducesFinding()
    {
        // 有快发形态的 trace：允许 1640–1760ms。真实 trace 第二段 = 10 帧快发 + 稳态，
        // 实测 2325ms（§14.6）。anchor=nmsts，快发只作为观测证据参与，
        // 所以这里额外钉住 EvidenceKind，证明快发确实被看到了。
        var burst = Enumerable.Range(0, 10).Select(i => i * 0.020).ToArray();
        var steady = Enumerable.Range(1, 4).Select(i => 0.180 + i * 0.536).ToArray();
        var frames = burst.Concat(steady).Select(t => FrameAt(t)).ToArray();

        var result = Evaluate(frames, Rule(min: 1640, max: 1760, anchor: "nmsts"));

        var f = Assert.Single(result.Findings);
        Assert.Equal("NM_REPEAT_MESSAGE_DURATION_EXCEEDED", f.Code);
        var obs = Assert.Single(result.Observations, o => o.Kind == "repeat_message_duration");
        Assert.Equal("measured", obs.EvidenceKind);
    }

    [Fact]
    public void RepeatMessageDuration_WithinLimit_NoFinding()
    {
        var frames = Steady(0.0, count: 4, stepSec: 0.42);   // 1.26s

        var result = Evaluate(frames, Rule(min: 1440, max: 1560, anchor: "nmsts"));

        Assert.Empty(result.Findings);
        // 原语根本没被求值时也会「无 finding」，必须钉住它确实求过值。
        Assert.Empty(result.Unmodeled);
    }

    [Fact]
    public void RepeatMessageDuration_SegmentCutByWindow_IsIndeterminate()
    {
        // 段延伸到窗口末尾仍未结束：时长只是下限，不能判超差。
        var frames = Steady(0.0, count: 6, stepSec: 0.42);

        var result = Evaluate(frames, Rule(min: 1440, max: 1560, anchor: "nmsts"),
            windowEnd: frames[^1].Frame.Timestamp);

        Assert.Empty(result.Findings);
        var obs = Assert.Single(result.Observations, o => o.Kind == "repeat_message_duration");
        // 截断时证据只能是「边界」，不能冒充实测值。
        Assert.Equal("boundary", obs.EvidenceKind);
    }

    [Fact]
    public void RepeatMessageDuration_NoNmstsRole_UnmodeledNoFinding()
    {
        // profile 未声明 NMSts 位时，首末帧跨度把 NOS 也算了进去，
        // 用它判超差会在「正常释放」场景误报（spec §7.2）：
        // 只能留观测并让规则显式进 Unmodeled，绝不能产 finding。
        var frames = Steady(0.0, count: 6, stepSec: 0.42);

        var result = Evaluate(frames, Rule(min: 1440, max: 1560, anchor: "nmsts"),
            withNmsts: false);

        Assert.Empty(result.Findings);
        Assert.Contains(result.Observations,
            o => o.Kind == "repeat_message_duration" && o.Detail.Contains("NMSts 不可读",
                StringComparison.Ordinal));
        var u = Assert.Single(result.Unmodeled);
        Assert.Equal("NM_06", u.Clause);
    }

    [Fact]
    public void RepeatMessageDuration_PureNos_NoFindingAndUnmodeled()
    {
        // 纯 NOS：稳态 500ms、NMSts=1 全程，整段没有 Repeat Message State 锚点。
        // 若回退到「首末帧跨度」判超差，正常运行的每一个 trace 都会被误报。
        var frames = Steady(0.0, count: 12, stepSec: 0.5, nmsts: 1);

        var result = Evaluate(frames, Rule(min: 1440, max: 1560, anchor: "nmsts"));

        Assert.Empty(result.Findings);
        var u = Assert.Single(result.Unmodeled);
        Assert.Equal("NM_06", u.Clause);
        Assert.Contains("未观察到 RMS 锚点", u.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatMessageDuration_EpisodeBoundaryEnd_IsIndeterminate()
    {
        // episode 自身被 trace 边界切断（BoundaryEnd），**且 NMSts=0 一直持续到
        // episode 末帧**：RMS 可能仍在继续，时长只是下限，不能判超差。
        // 注意「episode 被切断」本身不足以判定段内所有测量都不可判——那是 C1 的误判。
        double[] times = [0.0, 0.525, 1.05, 1.575, 2.1, 2.625, 3.1];
        var frames = times.Select(t => FrameAt(t)).ToArray();
        var episode = new NmEpisode(0, 0.0, frames[^1].Frame.Timestamp, "active", null,
            ParticipantA, BoundaryStart: false, BoundaryEnd: true);

        var result = Evaluate(frames, Rule(min: 1440, max: 1560, anchor: "nmsts"),
            episodes: new[] { episode }, windowEnd: 8.0);

        Assert.Empty(result.Findings);
        var obs = Assert.Single(result.Observations, o => o.Kind == "repeat_message_duration");
        Assert.Equal("boundary", obs.EvidenceKind);
    }

    [Fact]
    public void RepeatMessageDuration_RmsInLastEpisodeFollowedByNos_StillReportsFinding()
    {
        // 真实 trace §14.6 的第二段 RMS 形态：段落在**最后一个 episode** 里
        // （BoundaryEnd=true），但随后还有 NOS 帧，段终点并未触到 episode 末帧。
        // 「episode 被边界截断」不等于「段内任何测量都不可判」——否则第二段 RMS
        // 永远不会被检出（review C1）。
        var rms = Steady(0.0, count: 6, stepSec: 0.42);           // NMSts=0，0→2.1s
        var nos = Steady(2.6, count: 3, stepSec: 0.5, nmsts: 1);  // NMSts=1，2.6→3.6s
        var frames = rms.Concat(nos).ToArray();
        var episode = new NmEpisode(0, 0.0, frames[^1].Frame.Timestamp, "active", null,
            ParticipantA, BoundaryStart: false, BoundaryEnd: true);

        var result = Evaluate(frames, Rule(min: 1440, max: 1560, anchor: "nmsts"),
            episodes: new[] { episode }, windowEnd: 8.0);

        var f = Assert.Single(result.Findings);
        Assert.Equal("NM_REPEAT_MESSAGE_DURATION_EXCEEDED", f.Code);
        Assert.True(f.IsInference, "P 级原语必须标推断");
        Assert.Empty(result.Unmodeled);
    }

    [Fact]
    public void RepeatMessageDuration_BurstEndAnchor_NoBurst_UnmodeledNoObservation()
    {
        // anchor=burst_end 但没有快发段：不能悄悄退回 nmstsStart（那会改变规则要求的
        // 测量口径），必须显式不可求值。
        var frames = Steady(0.0, count: 6, stepSec: 0.42);

        var result = Evaluate(frames, Rule(min: 1440, max: 1560, anchor: "burst_end"));

        Assert.Empty(result.Findings);
        Assert.DoesNotContain(result.Observations, o => o.Kind == "repeat_message_duration");
        var u = Assert.Single(result.Unmodeled);
        Assert.Contains("未观察到快发段", u.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatMessageDuration_BurstEndAnchor_IndeterminateBurst_Unmodeled()
    {
        // 快发段尾触到 episode 边界：burst.End 只是下限，不能拿它当 RMS 起点。
        var burst = Enumerable.Range(0, 10).Select(i => i * 0.020).ToArray();
        var frames = burst.Select(t => FrameAt(t)).ToArray();
        var episode = new NmEpisode(0, 0.0, frames[^1].Frame.Timestamp, "active", null,
            ParticipantA, BoundaryStart: false, BoundaryEnd: false);

        var result = Evaluate(frames, Rule(min: 1440, max: 1560, anchor: "burst_end"),
            episodes: new[] { episode }, windowEnd: 8.0);

        Assert.Empty(result.Findings);
        Assert.DoesNotContain(result.Observations, o => o.Kind == "repeat_message_duration");
        var u = Assert.Single(result.Unmodeled);
        Assert.Contains("下限", u.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatMessageDuration_NonPositiveDuration_NoObservation()
    {
        // 快发段终点越过 NMSts=0 终点（尾部帧已是 NOS 且以短间隔发送）：
        // 算出的时长为负，绝不能写成观测，更不能判违规。
        double[] zeroTimes = [0.0, 0.42, 0.84, 1.26];
        double[] shortNosTimes = [1.36, 1.38, 1.40, 1.42];
        double[] tailTimes = [1.92, 2.42];
        var frames = zeroTimes.Select(t => FrameAt(t, 0))
            .Concat(shortNosTimes.Select(t => FrameAt(t, 1)))
            .Concat(tailTimes.Select(t => FrameAt(t, 1)))
            .ToArray();
        var episode = new NmEpisode(0, 0.0, frames[^1].Frame.Timestamp, "active", null,
            ParticipantA, BoundaryStart: false, BoundaryEnd: false);

        var result = Evaluate(frames, Rule(min: 1440, max: 1560, anchor: "burst_end"),
            episodes: new[] { episode }, windowEnd: 8.0);

        Assert.Empty(result.Findings);
        Assert.DoesNotContain(result.Observations, o => o.Kind == "repeat_message_duration");
        Assert.Contains(result.Unmodeled, u => u.Reason.Contains("非正", StringComparison.Ordinal));
    }

    [Fact]
    public void RepeatMessageDuration_NmstsToggleWithinEpisode_DoesNotFabricateFinding()
    {
        // 一个 episode 内 NMSts 0→1→0（两段 RMS 之间没有整段静默）：
        // 若用「首个 0 → 末个 0」跨越中间的 NOS，会把两段拼成 3.5s 的假 RMS 并误报超差。
        var firstRms = Steady(0.0, count: 3, stepSec: 0.2);         // 0→0.4s NMSts=0
        var nos = Steady(0.5, count: 4, stepSec: 0.5, nmsts: 1);    // 0.5→2.0s NMSts=1
        var secondRms = Steady(2.5, count: 3, stepSec: 0.5);        // 2.5→3.5s NMSts=0
        var frames = firstRms.Concat(nos).Concat(secondRms).ToArray();

        var result = Evaluate(frames, Rule(min: 1440, max: 1560, anchor: "nmsts"));

        Assert.Empty(result.Findings);
        var obs = Assert.Single(result.Observations, o => o.Kind == "repeat_message_duration");
        // 只取包含首个 NMSts=0 帧的连续段：0→0.4s。
        Assert.Equal(0.4, obs.End, 3);
        Assert.Empty(result.Unmodeled);
    }

    [Fact]
    public void RepeatMessageDuration_BurstEndAnchor_StartsAtBurstEnd()
    {
        // anchor=burst_end：RMS 起点取快发段结束，而不是首个 NMSts=0 帧。
        // 快发 0–0.18s 之后接稳态；整段 NMSts=0，实测窗口 1440–1560ms。
        var burst = Enumerable.Range(0, 10).Select(i => i * 0.020).ToArray();
        var steady = Enumerable.Range(1, 3).Select(i => 0.180 + i * 0.500).ToArray();
        var frames = burst.Concat(steady).Select(t => FrameAt(t)).ToArray();

        var result = Evaluate(frames, Rule(min: 1440, max: 1560, anchor: "burst_end"));

        // 起点 0.18s → 末帧 1.68s，实测 1500ms，落在允许窗内。
        Assert.Empty(result.Findings);
        var obs = Assert.Single(result.Observations, o => o.Kind == "repeat_message_duration");
        Assert.Equal(0.18, obs.Start, 3);
    }

    [Fact]
    public void RepeatMessageDuration_TwoCompliantNodes_NoFinding()
    {
        // C1 回归（final review）：同一 episode 内两个节点的帧交错出现。
        // 若把 episode 内全部帧合并成一个流，A 的 NMSts=0 段会被 B 在 1.8s 的帧
        // 拉长到 1800ms（> 1760），凭空误报超差，并把结论归给先到的 A。
        // 按 (CAN ID, 帧类型) 分组后：A 0.0→1.5 = 1500ms，B 0.8→1.8 = 1000ms，均合规。
        var a = new[]
        {
            NodeFrame(0.0, NmIdA, "A", nmsts: 0),
            NodeFrame(0.5, NmIdA, "A", nmsts: 0),
            NodeFrame(1.0, NmIdA, "A", nmsts: 0),
            NodeFrame(1.5, NmIdA, "A", nmsts: 0),
            NodeFrame(2.0, NmIdA, "A", nmsts: 1),
            NodeFrame(2.5, NmIdA, "A", nmsts: 1),
        };
        var b = new[]
        {
            NodeFrame(0.8, NmIdB, "B", nmsts: 0),
            NodeFrame(1.3, NmIdB, "B", nmsts: 0),
            NodeFrame(1.8, NmIdB, "B", nmsts: 0),
            NodeFrame(2.3, NmIdB, "B", nmsts: 1),
            NodeFrame(2.8, NmIdB, "B", nmsts: 1),
        };
        var frames = a.Concat(b).OrderBy(f => f.Frame.Timestamp).ToArray();

        var result = Evaluate(frames, Rule(min: 1440, max: 1760, anchor: "nmsts"));

        Assert.Empty(result.Findings);
        Assert.Empty(result.Unmodeled);
    }

    [Fact]
    public void RepeatMessageDuration_OneNodeOverLimit_ReportsOnlyThatNode()
    {
        // 按报文分组后每个节点独立度量：A 的 RMS 0.0→2.1 = 2100ms（超 1760），
        // B 的 RMS 0.8→2.3 = 1500ms（合规）。只允许一条 finding，且必须点名 A。
        var a = new[]
        {
            NodeFrame(0.0, NmIdA, "A", nmsts: 0),
            NodeFrame(0.5, NmIdA, "A", nmsts: 0),
            NodeFrame(1.0, NmIdA, "A", nmsts: 0),
            NodeFrame(1.5, NmIdA, "A", nmsts: 0),
            NodeFrame(2.0, NmIdA, "A", nmsts: 0),
            NodeFrame(2.1, NmIdA, "A", nmsts: 0),
            NodeFrame(2.6, NmIdA, "A", nmsts: 1),
        };
        var b = new[]
        {
            NodeFrame(0.8, NmIdB, "B", nmsts: 0),
            NodeFrame(1.3, NmIdB, "B", nmsts: 0),
            NodeFrame(1.8, NmIdB, "B", nmsts: 0),
            NodeFrame(2.3, NmIdB, "B", nmsts: 0),
            NodeFrame(2.8, NmIdB, "B", nmsts: 1),
        };
        var frames = a.Concat(b).OrderBy(f => f.Frame.Timestamp).ToArray();

        var result = Evaluate(frames, Rule(min: 1440, max: 1760, anchor: "nmsts"));

        var finding = Assert.Single(result.Findings);
        Assert.Equal("NM_REPEAT_MESSAGE_DURATION_EXCEEDED", finding.Code);
        Assert.Contains("0x5F4", finding.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("0x5F5", finding.Message, StringComparison.Ordinal);
        // 证据区间必须来自 A 自己的 RMS：0.0→2.1 = 2100ms。
        Assert.Equal(2.1, finding.EvidenceTimes[1] - finding.EvidenceTimes[0], 6);
    }

    [Fact]
    public void RepeatMessageDuration_RealTraceShape_AttributesToBmsNotPms()
    {
        // 真实 trace §14.3 形态：PMS 在 +0.000s 先到，BMS 从 +0.1s 起形成 2100ms RMS。
        // 合并帧流会把 frames[0] 的 PMS 当作被测节点，finding 点名错误节点。
        // 分组后 PMS 只有一帧（不成段，跳过），结论必须归给真正形成 RMS 的 BMS。
        var pms = new[] { NodeFrame(0.0, NmIdPms, "PMS", nmsts: 0) };
        var bms = new[]
        {
            NodeFrame(0.1, NmIdBms, "BMS", nmsts: 0),
            NodeFrame(0.6, NmIdBms, "BMS", nmsts: 0),
            NodeFrame(1.1, NmIdBms, "BMS", nmsts: 0),
            NodeFrame(1.6, NmIdBms, "BMS", nmsts: 0),
            NodeFrame(2.1, NmIdBms, "BMS", nmsts: 0),
            NodeFrame(2.2, NmIdBms, "BMS", nmsts: 0),
            NodeFrame(2.7, NmIdBms, "BMS", nmsts: 1),
        };
        var frames = pms.Concat(bms).OrderBy(f => f.Frame.Timestamp).ToArray();

        var result = Evaluate(frames, Rule(min: 1440, max: 1760, anchor: "nmsts"));

        var finding = Assert.Single(result.Findings);
        Assert.Contains("0x5F5", finding.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("0x5F4", finding.Message, StringComparison.Ordinal);
        Assert.Equal(2.1, finding.EvidenceTimes[1] - finding.EvidenceTimes[0], 6);
    }

    // ==== first_frame_latency ====

    [Fact]
    public void FirstFrameLatency_BeyondMax_ProducesFinding()
    {
        // episode 从 0 开始，首帧在 0.3s，允许 10ms。
        var frames = Steady(0.3, count: 4, stepSec: 0.5);
        var episode = new NmEpisode(0, 0.0, 2.0, "active", null,
            ParticipantA, false, false);

        var result = Evaluate(frames,
            new NmRuleDef("NM_02", "首帧延迟", "wakeup",
                new NmRuleWhen("first_frame_latency", LatencyKind: "start_nm_tx"),
                new NmRuleExpect("within_pct", MaxMs: 10),
                "NM_FIRST_FRAME_LATENCY"),
            episodes: new[] { episode });

        var f = Assert.Single(result.Findings);
        Assert.True(f.IsInference, "P 级原语必须标推断");
    }

    [Fact]
    public void FirstFrameLatency_WithinMax_NoFinding()
    {
        // 首帧在 0.005s，允许 10ms。
        var frames = Steady(0.005, count: 4, stepSec: 0.5);
        var episode = new NmEpisode(0, 0.0, 2.0, "active", null,
            ParticipantA, false, false);

        var result = Evaluate(frames,
            new NmRuleDef("NM_02", "首帧延迟", "wakeup",
                new NmRuleWhen("first_frame_latency", LatencyKind: "start_nm_tx"),
                new NmRuleExpect("within_pct", MaxMs: 10),
                "NM_FIRST_FRAME_LATENCY"),
            episodes: new[] { episode });

        Assert.Empty(result.Findings);
        // 原语根本没被求值时也会「无 finding」，必须钉住它确实求过值。
        Assert.Empty(result.Unmodeled);
    }

    [Fact]
    public void FirstFrameLatency_UnknownLatencyKind_UnmodeledNoFinding()
    {
        // latency_kind 打错字不能被静默接受：必须显式不可求值并给出合法取值。
        var frames = Steady(0.3, count: 4, stepSec: 0.5);

        var result = Evaluate(frames,
            new NmRuleDef("NM_02", "首帧延迟", "wakeup",
                new NmRuleWhen("first_frame_latency", LatencyKind: "start_nm_txx"),
                new NmRuleExpect("within_pct", MaxMs: 10),
                "NM_FIRST_FRAME_LATENCY"));

        Assert.Empty(result.Findings);
        var u = Assert.Single(result.Unmodeled);
        Assert.Contains("start_nm_tx", u.Reason, StringComparison.Ordinal);
        Assert.Contains("wakeup", u.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void FirstFrameLatency_NoSeparateStartAnchor_Unmodeled()
    {
        // 生产路径的 episode.Start 就是首帧时间：没有独立的 wakeup/启动锚点。
        // 测得的 0 不是「延迟为 0」，必须显式不可求值，不能当作通过。
        var frames = Steady(1.0, count: 4, stepSec: 0.5);
        var episode = new NmEpisode(0, frames[0].Frame.Timestamp,
            frames[^1].Frame.Timestamp + 1.0, "active", null,
            ParticipantA, BoundaryStart: false, BoundaryEnd: false);

        var result = Evaluate(frames,
            new NmRuleDef("NM_02", "首帧延迟", "wakeup",
                new NmRuleWhen("first_frame_latency", LatencyKind: "wakeup"),
                new NmRuleExpect("within_pct", MaxMs: 10),
                "NM_FIRST_FRAME_LATENCY"),
            episodes: new[] { episode });

        Assert.Empty(result.Findings);
        var u = Assert.Single(result.Unmodeled);
        Assert.Contains("首帧延迟不可测", u.Reason, StringComparison.Ordinal);
    }

    private static NmRuleDef Rule(int min, int max, string anchor)
        => new("NM_06", "RMS 持续时长", "wakeup",
            new NmRuleWhen("repeat_message_duration", CycleMs: 500),
            new NmRuleExpect("duration_within_ms", MinMs: min, MaxMs: max, Anchor: anchor),
            "NM_REPEAT_MESSAGE_DURATION_EXCEEDED");

    /// <summary>
    /// 带 NMSts 位的 profile：byte3.bit0。测试帧把 NMSts 恒写 0
    /// （即整段都是 Repeat Message State），这样时长判定有确定锚点。
    /// </summary>
    private static NmRuleResult Evaluate(
        IReadOnlyList<NmFrame> frames, NmRuleDef rule,
        IReadOnlyList<NmEpisode>? episodes = null,
        double? windowEnd = null,
        bool withNmsts = true)
    {
        var defs = frames.Select(f => f.Def).Distinct().ToArray();
        var profile = new NmProfile(
            NmProfileSource.Dbc, defs, new NmTimings(CycleMs: 500, TimeoutMs: 2000),
            Array.Empty<NmNodeDef>(), Array.Empty<NmConfigConflict>(),
            Array.Empty<string>(), new NmProvenance(),
            Layout: new NmLayout(NidByte: 0, CbvByte: 1),
            StandardBitRoles: withNmsts ? new[] { new NmBitRole(3, 0, "NmSts") } : null);
        var timing = NmTimingDefaults.FromProfile(profile.Timings, null);
        // 默认 episode 的终点必须**大于**末帧，否则「段尾触边界 → 时长只是下限」
        // 的抑制会吞掉全部判定，超差用例永远红。只有显式要验截断的用例
        // 才让 episode.End 等于末帧（见 RepeatMessageDuration_SegmentCutByWindow）。
        var eps = episodes ?? new[]
        {
            new NmEpisode(0, frames[0].Frame.Timestamp, frames[^1].Frame.Timestamp + 1.0,
                "active", null, ParticipantA, false, false),
        };
        var ctx = new NmRuleContext(frames, eps, profile, timing,
            new NmWindow(0, windowEnd ?? frames[^1].Frame.Timestamp + 5, "test"))
        { Rules = new[] { rule } };
        return NmRuleEngine.Evaluate(ctx.Rules, ctx);
    }

    private static NmFrame[] Steady(double start, int count, double stepSec, byte nmsts = 0)
        => Enumerable.Range(0, count).Select(i => FrameAt(start + i * stepSec, nmsts)).ToArray();

    // byte3 = nmsts → 0 表示 Repeat Message State，1 表示 Normal Operation State。
    // byte0 = 0x00 → 标准帧 0x500 的期望 NID，(0x500-0x400)&0xFF = 0x00。
    private static NmFrame FrameAt(double t, byte nmsts = 0)
        => new(new NmMessageDef("0x500", false, "NM", "A", 8, true,
                TestReasons, CycleMs: 500, TimeoutMs: 2000),
            new ReplayFrame(t, 0x500, 8,
                new byte[] { 0x00, 0, 0, nmsts, 0, 0, 0, 0 },
                PeakCan.HIL.Core.FrameFlags.None));

    /// <summary>指定 CAN ID / 发送者的标准 NM 帧，用于多报文（多节点）回归用例。</summary>
    private static NmFrame NodeFrame(double t, uint id, string sender, byte nmsts)
        => new(new NmMessageDef($"0x{id:X3}", false, "NM", sender, 8, true,
                TestReasons, CycleMs: 500, TimeoutMs: 2000),
            new ReplayFrame(t, id, 8,
                new byte[] { 0x00, 0, 0, nmsts, 0, 0, 0, 0 },
                PeakCan.HIL.Core.FrameFlags.None));
}
