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
    public void RepeatMessageDuration_WithBurst_OverLimit_ProducesFinding()
    {
        // 有快发：允许 1640–1760ms。真实 trace 第二段 = 10 帧快发 + 稳态，
        // 实测 2325ms（§14.6）。这里构造同样的形态：快发段之后接 536ms 稳态。
        var burst = Enumerable.Range(0, 10).Select(i => i * 0.020).ToArray();
        var steady = Enumerable.Range(1, 4).Select(i => 0.180 + i * 0.536).ToArray();
        var frames = burst.Concat(steady).Select(t => FrameAt(t)).ToArray();

        var result = Evaluate(frames, Rule(min: 1640, max: 1760, anchor: "nmsts"));

        var f = Assert.Single(result.Findings);
        Assert.Equal("NM_REPEAT_MESSAGE_DURATION_EXCEEDED", f.Code);
    }

    [Fact]
    public void RepeatMessageDuration_WithinLimit_NoFinding()
    {
        var frames = Steady(0.0, count: 4, stepSec: 0.42);   // 1.26s

        var result = Evaluate(frames, Rule(min: 1440, max: 1560, anchor: "nmsts"));

        Assert.Empty(result.Findings);
    }

    [Fact]
    public void RepeatMessageDuration_SegmentCutByWindow_IsIndeterminate()
    {
        // 段延伸到窗口末尾仍未结束：时长只是下限，不能判超差。
        var frames = Steady(0.0, count: 6, stepSec: 0.42);

        var result = Evaluate(frames, Rule(min: 1440, max: 1560, anchor: "nmsts"),
            windowEnd: frames[^1].Frame.Timestamp);

        Assert.Empty(result.Findings);
        Assert.Contains(result.Observations, o => o.Kind == "repeat_message_duration");
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
        // episode 自身被 trace 边界切断（BoundaryEnd）：同样只能给下限。
        var frames = Steady(0.0, count: 6, stepSec: 0.42);
        var episode = new NmEpisode(0, frames[0].Frame.Timestamp,
            frames[^1].Frame.Timestamp + 1.0, "active", null,
            ParticipantA, BoundaryStart: false, BoundaryEnd: true);

        var result = Evaluate(frames, Rule(min: 1440, max: 1560, anchor: "burst_end"),
            episodes: new[] { episode }, windowEnd: frames[^1].Frame.Timestamp + 5);

        Assert.Empty(result.Findings);
        Assert.Contains(result.Observations, o => o.Kind == "repeat_message_duration");
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
    }

    // ==== 构造辅助 ====

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
}
