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
        // 「Only」必须有反向断言：不可观测条款只能进 Unmodeled，绝不能产 finding。
        Assert.DoesNotContain(report.Findings, f => f.RuleId == "NM_U1");
    }

    /// <summary>
    /// 取消时规则引擎不运行，M1 的周期偏差结论不能被无声丢弃（spec §9.4）。
    /// 去重必须发生在引擎求值之后：本次没有规则副本，M1 副本必须保留在 partial 报告里。
    /// </summary>
    [Fact]
    public void Analyze_CancelDuringMessageLoop_KeepsM1CycleDeviationInPartialReport()
    {
        using var cts = new CancellationTokenSource();
        // 0x500 有 6 帧、间隔 700ms（期望 500ms），M1 会判周期偏差；
        // 0x501 只是让逐报文循环出现第二次检查点，用于在首条报文完成后取消。
        var frames = new[]
        {
            FrameAt(0.0), FrameAt(0.7), FrameAt(1.4),
            FrameAt(2.1), FrameAt(2.8), FrameAt(3.5),
            FrameAt(0.0, 0x501), FrameAt(0.7, 0x501),
        };

        var loopChecks = 0;
        var report = NmAnalyzer.Analyze(
            new NmAnalysisRequest("src", null, frames, null, CycleProfile(twoMessages: true), 0, 4.0),
            checkpoint =>
            {
                if (checkpoint == NmAnalyzerCheckpoint.MessageLoop && ++loopChecks == 2)
                    cts.Cancel();
            },
            cts.Token);

        Assert.True(report.IsPartial);
        var f = Assert.Single(report.Findings, x => x.Code == "NM_CYCLE_DEVIATION");
        // 幸存者是 M1 副本（RuleId 为 null）：规则副本根本没机会产出。
        Assert.Null(f.RuleId);
    }

    /// <summary>spec §6.1 rule 6：报告自述来源 profile（standard_id@version）。</summary>
    [Fact]
    public void Analyze_WithStandardProfile_ReportsStandardIdAndNullHash()
    {
        var frames = new[] { FrameAt(0.0) };

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, frames, null, Profile(rules: false), 0, 1.0,
            Standard: new NmStandardProfile { StandardId = "geely-cv", Version = "2.0" }));

        Assert.Equal("geely-cv@2.0", report.StandardId);
        // canonical 序列化器缺失（spec §10.1.1），哈希必须显式留空而不是伪造。
        Assert.Null(report.ProfileHash);
    }

    [Fact]
    public void Analyze_WithoutStandardProfile_ReportsNullStandardId()
    {
        var frames = new[] { FrameAt(0.0) };

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, frames, null, Profile(rules: false), 0, 1.0));

        Assert.Null(report.StandardId);
        Assert.Null(report.ProfileHash);
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

    /// <summary>
    /// 去重只能按具体码，不能泛化：NM_NID_MISMATCH 由 M1 与 NM_01 两条路径产出，
    /// 但判据不同（M1 比 DBC 站地址，NM_01 比 CAN ID 低字节，spec §14.4）。
    /// 0x500 的 NID 符合自己的 CAN ID 映射，只有 M1 报站地址不符；
    /// 0x501 的 NID 违反映射，让 NM_01 产出同码 finding。按码一刀切会连 0x500 的
    /// 不同结论一起删掉——本用例锁死这一条。
    /// </summary>
    [Fact]
    public void Analyze_NidMismatch_KeepsBothM1AndRuleFindings()
    {
        // 0x500：NID 0x00 == (0x500-0x400)&0xFF，但 ≠ 站地址 0xF4 → 仅 M1 报。
        // 0x501：NID 0x99 ≠ (0x501-0x400)&0xFF = 0x01 → NM_01 报。
        var frames = new[]
        {
            FrameAt(0.0, 0x500, nid: 0x00),
            FrameAt(0.5, 0x501, nid: 0x99),
        };

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, frames, null, NidRuleProfile(twoMessages: true), 0, 4.0));

        // M1 关于 0x500 的结论（RuleId 为 null）必须保留，不能因为另一报文
        // 触发了同码规则 finding 就被按码删除。
        var m1 = Assert.Single(report.Findings, f =>
            f.Code == "NM_NID_MISMATCH" && f.RuleId is null
            && f.Message.Contains("NID 0x00", StringComparison.Ordinal));
        Assert.Null(m1.RuleId);
        // 规则关于 0x501 的结论也在。
        Assert.Contains(report.Findings, f => f.Code == "NM_NID_MISMATCH" && f.RuleId == "NM_01");
    }

    /// <summary>
    /// NID 同时违反两边判据时，两条路径各自的结论都必须保留：
    /// 去重不得泛化到 NM_NID_MISMATCH（它们判的不是同一件事）。
    /// </summary>
    [Fact]
    public void Analyze_BothNidCriteriaViolated_KeepsBothFindings()
    {
        // NID 0x99：≠ 站地址 0xF4（M1），且 ≠ CAN ID 低字节 0x00（NM_01）。
        var frames = new[] { FrameAt(0.0, 0x500, nid: 0x99) };

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, frames, null, NidRuleProfile(), 0, 4.0));

        var nidFindings = report.Findings.Where(f => f.Code == "NM_NID_MISMATCH").ToArray();
        Assert.Contains(nidFindings, f => f.RuleId is null);
        Assert.Contains(nidFindings, f => f.RuleId == "NM_01");
    }

    /// <summary>
    /// 周期去重只应抑制同码的 M1 周期副本；同一次运行里 M1 的 NM_NID_MISMATCH
    /// 与 NM_01 的副本判据不同，不能被一起清掉。
    /// </summary>
    [Fact]
    public void Analyze_RuleProducesCycleFinding_SuppressesOnlyM1CycleCopy()
    {
        var frames = new[]
        {
            FrameAt(0.0, nid: 0x99), FrameAt(0.7, nid: 0x99), FrameAt(1.4, nid: 0x99),
            FrameAt(2.1, nid: 0x99), FrameAt(2.8, nid: 0x99), FrameAt(3.5, nid: 0x99),
        };

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, frames, null, NidRuleProfile(withCycleRule: true), 0, 4.0));

        // 周期偏差只剩规则副本（带条款追溯）。
        var cycle = Assert.Single(report.Findings, f => f.Code == "NM_CYCLE_DEVIATION");
        Assert.Equal("NM_CYCLE", cycle.RuleId);

        // 同一次运行中的 NID 结论：M1 与规则都保留。
        Assert.Contains(report.Findings, f => f.Code == "NM_NID_MISMATCH" && f.RuleId is null);
        Assert.Contains(report.Findings, f => f.Code == "NM_NID_MISMATCH" && f.RuleId == "NM_01");
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

    private static NmProfile CycleProfile(bool twoMessages = false)
    {
        var messages = new List<NmMessageDef>
        {
            new("0x500", false, "NM", "A", 8, true,
                TestReasons, CycleMs: 500, TimeoutMs: 2000),
        };
        if (twoMessages)
        {
            messages.Add(new NmMessageDef("0x501", false, "NM2", "A", 8, true,
                TestReasons, CycleMs: 500, TimeoutMs: 2000));
        }

        return new NmProfile(
            NmProfileSource.Dbc, messages, new NmTimings(CycleMs: 500, TimeoutMs: 2000),
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

    /// <summary>
    /// NID 规则 profile：站地址固定 0xF4，NID 在 byte0。0x500 的 CAN ID 低字节映射
    /// 期望 NID 为 0x00（标准帧 ID-0x400），因此「NID=0x00」只违反 M1 的站地址判据，
    /// 不违反 NM_01；「NID=0x99」两边都违反。
    /// </summary>
    private static NmProfile NidRuleProfile(bool twoMessages = false, bool withCycleRule = false)
    {
        var messages = new List<NmMessageDef>
        {
            new("0x500", false, "NM", "A", 8, true,
                TestReasons, CycleMs: 500, TimeoutMs: 2000),
        };
        if (twoMessages)
        {
            messages.Add(new NmMessageDef("0x501", false, "NM2", "A", 8, true,
                TestReasons, CycleMs: 500, TimeoutMs: 2000));
        }

        var rules = new List<NmRuleDef>
        {
            new("NM_01", "NID 与 ID 映射一致", "normal",
                new NmRuleWhen("nid_id_mapping"), new NmRuleExpect("matches_id_low_byte"),
                "NM_NID_MISMATCH")
            {
                SourceRef = "§14.4",
            },
        };
        if (withCycleRule)
        {
            rules.Add(new NmRuleDef("NM_CYCLE", "NM 报文应按周期发送", "normal",
                new NmRuleWhen("cycle_tolerance"), new NmRuleExpect("within_pct"),
                "NM_CYCLE_DEVIATION")
            {
                SourceRef = "§7.4",
            });
        }

        return new NmProfile(
            NmProfileSource.Dbc, messages, new NmTimings(CycleMs: 500, TimeoutMs: 2000),
            new[] { new NmNodeDef("A", 0xF4, null, true) },
            Array.Empty<NmConfigConflict>(), Array.Empty<string>(), new NmProvenance(),
            Layout: new NmLayout(NidByte: 0, CbvByte: 1),
            StandardTimings: withCycleRule
                ? new NmStandardTimings
                {
                    CycleMs = 500, TimeoutMs = 2000, ToleranceRatio = 0.1,
                }
                : null,
            StandardRules: rules);
    }

    // byte0 可指定 NID；默认 0x00 是标准帧 0x500/0x501 之外的合法值，
    // 现有用例不关心 NID 时保持 0x00 即可（其 profile 未配节点表，M1 不报 NID）。
    private static ReplayFrame FrameAt(double t, uint id = 0x500, byte nid = 0x00)
        => new(t, id, 8, new byte[] { nid, 0, 0, 0, 0, 0, 0, 0 },
            PeakCan.HIL.Core.FrameFlags.None);
}
