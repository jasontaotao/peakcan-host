using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// spec §12：合成 fixture 上「注入故障必触发、合法场景零误报」。
/// 真实 trace 只验证据可解释，不拿它调规则。
/// </summary>
public class NmRuleIntegrationTests
{
    private static readonly string[] TestReasons = ["test"];

    [Fact]
    public void Analyze_ReservedBitSet_ProducesRuleFindingWithTraceability()
    {
        var frames = new[]
        {
            new ReplayFrame(0.0, 0x500, 8, new byte[] { 0xF4, 0x01, 0, 0, 0, 0, 0, 0 },
                PeakCan.HIL.Core.FrameFlags.None),
        };

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, frames, null, Profile(rules: true), 0, 1.0));

        var f = Assert.Single(report.Findings, x => x.Code == "NM_RMR_SET_WHILE_DISABLED");
        // 条款可追溯：报告必须带规则号与出处。
        Assert.Equal("NM_05", f.RuleId);
        Assert.Equal("§5.4.3", f.SourceRef);
    }

    [Fact]
    public void Analyze_NoStandardProfile_NoRuleFindings()
    {
        var frames = new[]
        {
            new ReplayFrame(0.0, 0x500, 8, new byte[] { 0xF4, 0x01, 0, 0, 0, 0, 0, 0 },
                PeakCan.HIL.Core.FrameFlags.None),
        };

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, frames, null, Profile(rules: false), 0, 1.0));

        Assert.DoesNotContain(report.Findings, x => x.RuleId is not null);
        Assert.True(report.UnmodeledRules is null || report.UnmodeledRules.Count == 0);
    }

    [Fact]
    public void Analyze_UnobservableClause_AppearsInUnmodeledOnly()
    {
        var frames = new[]
        {
            new ReplayFrame(0.0, 0x500, 8, new byte[] { 0xF4, 0x00, 0, 0, 0, 0, 0, 0 },
                PeakCan.HIL.Core.FrameFlags.None),
        };

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, frames, null, Profile(rules: true), 0, 1.0));

        Assert.NotNull(report.UnmodeledRules);
        Assert.Contains(report.UnmodeledRules!, u => u.Clause == "NM_U1");
    }

    /// <summary>
    /// 企标选中时周期偏差只能报一次（spec §7.4 / R27）。幸存者必须是规则副本，
    /// 否则报告丢掉 RuleId/SourceRef，条款追溯断链。
    /// </summary>
    [Fact]
    public void Analyze_StandardCycleRuleActive_ReportsCycleDeviationOnce()
    {
        // 3.5s 内 6 帧、间隔 700ms：期望 500ms，容差 max(0.1×500, 30)=50ms，
        // M1 与 NM_CYCLE 规则都会判偏差。
        var frames = new[]
        {
            FrameAt(0.0), FrameAt(0.7), FrameAt(1.4),
            FrameAt(2.1), FrameAt(2.8), FrameAt(3.5),
        };

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, frames, null, CycleProfile(), 0, 4.0));

        var f = Assert.Single(report.Findings, x => x.Code == "NM_CYCLE_DEVIATION");
        // 规则副本带规则号与出处；M1 的匿名副本已被抑制。
        Assert.Equal("NM_CYCLE", f.RuleId);
        Assert.Equal("§7.4", f.SourceRef);
    }

    private static NmProfile Profile(bool rules)
    {
        var def = new NmMessageDef("0x500", false, "NM", "A", 8, true,
            TestReasons, CycleMs: 500, TimeoutMs: 2000);
        return new NmProfile(
            NmProfileSource.Dbc, new[] { def }, new NmTimings(CycleMs: 500, TimeoutMs: 2000),
            new[] { new NmNodeDef("A", 0xF4, null, true) },
            Array.Empty<NmConfigConflict>(), Array.Empty<string>(), new NmProvenance(),
            Layout: new NmLayout(NidByte: 0, CbvByte: 1),
            StandardBitRoles: rules
                ? new[]
                {
                    new NmBitRole(1, 0, "RepeatMessageRequest")
                        { Constraint = NmBitConstraint.MustBeZeroWhileDisabled },
                }
                : null,
            StandardRules: rules
                ? new[]
                {
                    new NmRuleDef("NM_05", "预留位必须为 0", "normal",
                        new NmRuleWhen("bit_rule", Role: "RepeatMessageRequest"),
                        new NmRuleExpect("is_zero"), "NM_RMR_SET_WHILE_DISABLED")
                    {
                        SourceRef = "§5.4.3",
                    },
                    new NmRuleDef("NM_U1", "内部定时器", "normal",
                        new NmRuleWhen("stop_sending"), new NmRuleExpect("observe"), "")
                    {
                        Observability = NmObservability.Unobservable,
                    },
                }
                : null);
    }

    private static NmProfile CycleProfile()
    {
        var def = new NmMessageDef("0x500", false, "NM", "A", 8, true,
            TestReasons, CycleMs: 500, TimeoutMs: 2000);
        return new NmProfile(
            NmProfileSource.Dbc, new[] { def }, new NmTimings(CycleMs: 500, TimeoutMs: 2000),
            Array.Empty<NmNodeDef>(), Array.Empty<NmConfigConflict>(),
            Array.Empty<string>(), new NmProvenance(),
            Layout: new NmLayout(NidByte: 0, CbvByte: 1),
            StandardTimings: new NmStandardTimings
            {
                CycleMs = 500, TimeoutMs = 2000, ToleranceRatio = 0.1,
            },
            StandardRules:
            [
                new NmRuleDef("NM_CYCLE", "NM 报文应按周期发送", "normal",
                    new NmRuleWhen("cycle_tolerance"),
                    new NmRuleExpect("within_pct"), "NM_CYCLE_DEVIATION")
                {
                    SourceRef = "§7.4",
                },
            ]);
    }

    // byte0 = 0x00：标准帧 0x500 的期望 NID（(0x500-0x400)&0xFF）。
    private static ReplayFrame FrameAt(double t)
        => new(t, 0x500, 8, new byte[] { 0x00, 0, 0, 0, 0, 0, 0, 0 },
            PeakCan.HIL.Core.FrameFlags.None);
}
