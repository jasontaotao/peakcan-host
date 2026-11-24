using PeakCan.Host.Core.Analysis.Nm;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// 标定面板的规则表（spec §10.2）：每条规则 → 触发 / 未触发 / Unmodeled。
/// <para>
/// <b>三态映射必须只依赖 RuleId，不依赖文案</b>：finding 的 message 是给人看的，
/// 会随文案调整变化；RuleId 才是身份。用文案判断会在改一句措辞后静默把
/// 「触发」变成「未触发」——这正是标定面板最不能出的错。
/// </para>
/// </summary>
public class NmCalibrationTableTests
{
    private static readonly string[] Clauses55 = ["§5.5"];
    private static readonly string[] Clauses54 = ["§5.4.2"];

    private static NmRuleDef Rule(string id, string when = "cycle_tolerance")
        => new(id, "req " + id, "normal",
            new NmRuleWhen(when, CycleMs: 500),
            new NmRuleExpect("within_pct", Pct: 0.1),
            "CODE_" + id)
        {
            Clauses = when == "nid_id_mapping" ? Clauses54 : Clauses55,
        };

    private static NmReport Report(
        IReadOnlyList<NmFinding>? findings = null,
        IReadOnlyList<NmObservation>? observations = null,
        IReadOnlyList<NmUnmodeledRule>? unmodeled = null,
        IReadOnlyList<NmDataQualityCheck>? checks = null)
        => new(
            new NmWindow(0, 10, "full"), "src", null,
            Array.Empty<NmEpisode>(), Array.Empty<NmMessageReport>(), Array.Empty<NmNodeReport>(),
            Array.Empty<NmEvent>(),
            findings ?? Array.Empty<NmFinding>(),
            Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<NmCapabilityState>(), Array.Empty<NmInputMissing>(),
            Array.Empty<NmTruncation>(),
            Observations: observations,
            UnmodeledRules: unmodeled,
            DataQualityChecks: checks);

    private static NmFinding Finding(string? ruleId, double at = 1.5)
        => new("NM_CYCLE_DEVIATION", "warn", "周期偏差", [at], false, null, RuleId: ruleId);

    private static NmObservation Observation(string? ruleId, double start = 0, double end = 10)
        => new("cycle_observed", null, "0x500", start, end, "实测 520ms", "measured",
            IsInference: false, RuleId: ruleId);

    private static NmUnmodeledRule Unmodeled(string? ruleId, string code = NmUnmodeledCodes.NotApplicable)
        => new("§5.5", "trace 里没有该事件") { RuleId = ruleId, ReasonCode = code };

    // —— 三态 ——

    [Fact]
    public void RuleWithFinding_IsTriggered()
    {
        var rows = NmCalibrationTable.Build(
            [Rule("NM_05")], Report(findings: [Finding("NM_05")]));

        var row = Assert.Single(rows);
        Assert.Equal("NM_05", row.RuleId);
        Assert.Equal(NmRuleStatus.Triggered, row.Status);
    }

    [Fact]
    public void RuleWithObservationOnly_IsNotTriggered()
    {
        // 有观测无 finding = 这条规则跑了、检查了、没发现违规。
        // 这正是「未触发」与「没跑」的分界：必须能区分。
        var rows = NmCalibrationTable.Build(
            [Rule("NM_05")], Report(observations: [Observation("NM_05")]));

        var row = Assert.Single(rows);
        Assert.Equal(NmRuleStatus.NotTriggered, row.Status);
        Assert.Equal(1, row.ObservationCount);
    }

    /// <summary>
    /// 部分原语会「一部分报文判成了、另一部分判不了」——同时有 observation 和
    /// unmodeled 条目。<see cref="NmRuleStatus.Unmodeled"/> 必须优先于
    /// <see cref="NmRuleStatus.NotTriggered"/>，否则状态列显示「未触发」
    /// 却带着 Unmodeled 原因，并与「未建模条款」区重复出现。
    /// </summary>
    [Fact]
    public void RuleWithBothObservationAndUnmodeled_IsUnmodeled()
    {
        var rows = NmCalibrationTable.Build(
            [Rule("NM_NMSTS", "nmsts_transition")],
            Report(
                observations: [Observation("NM_NMSTS")],
                unmodeled: [Unmodeled("NM_NMSTS", NmUnmodeledCodes.PartialEvidence)]));

        var row = Assert.Single(rows);
        Assert.Equal(NmRuleStatus.Unmodeled, row.Status);
        Assert.Equal(NmUnmodeledCodes.PartialEvidence, row.ReasonCode);
        // 观测仍然保留，用户能看到「已判成的那部分」。
        Assert.Equal(1, row.ObservationCount);
    }

    [Fact]
    public void RuleWithUnmodeled_IsUnmodeled()
    {
        var rows = NmCalibrationTable.Build(
            [Rule("NM_05")], Report(unmodeled: [Unmodeled("NM_05")]));

        var row = Assert.Single(rows);
        Assert.Equal(NmRuleStatus.Unmodeled, row.Status);
        Assert.Equal(NmUnmodeledCodes.NotApplicable, row.ReasonCode);
    }

    /// <summary>
    /// <b>关键场景</b>：部分原语（<c>layout_check</c> / <c>cycle_tolerance</c> /
    /// <c>burst_shape</c>）在**通过时不产任何 finding 也不产 observation**——
    /// 它们只在违规时往 findings 里加条目。
    /// <para>
    /// 若三态映射只看「有没有 finding/observation」，这些规则合规时会显示成
    /// 「未建模」，让用户以为标定面板漏了一条本来检查过的规则。这是标定面板
    /// 最不能出的错：把「查过没问题」报成「没查」。
    /// </para>
    /// <para>
    /// 判据必须包含「这条规则不在 Unmodeled 列表里」——引擎跑完没进 Unmodeled
    /// 就意味着求值器返回了 true 且没给原因，即「跑过了」。
    /// </para>
    /// </summary>
    [Fact]
    public void RuleEvaluatedButSilent_IsNotTriggered_NotUnmodeled()
    {
        // 该规则既无 finding 也无 observation 也无 unmodeled：
        // 求值器返回 true 且未给 unmodeledReason → 确实跑过且未违规。
        var rows = NmCalibrationTable.Build(
            [Rule("NM_LAYOUT", "layout_check")], Report());

        var row = Assert.Single(rows);
        Assert.Equal(NmRuleStatus.NotTriggered, row.Status);
    }

    [Fact]
    public void RuleInUnmodeledList_StaysUnmodeled_EvenWithoutFindings()
    {
        // 对照组：引擎明确把它放进 Unmodeled（求值器给了原因）时，
        // 不能因为「没 finding」就当合规。
        var rows = NmCalibrationTable.Build(
            [Rule("NM_05")],
            Report(unmodeled: [Unmodeled("NM_05", NmUnmodeledCodes.MissingProfileData)]));

        var row = Assert.Single(rows);
        Assert.Equal(NmRuleStatus.Unmodeled, row.Status);
        Assert.Equal(NmUnmodeledCodes.MissingProfileData, row.ReasonCode);
    }

    [Fact]
    public void RuleAbsentFromReport_IsUnmodeled()
    {
        // 规则既不在 findings/observations，也不在 unmodeled：
        // 说明引擎**根本没跑它**（例如报告是 partial、规则是后来加的）。
        // 这种情况必须报「未建模」而不是「未触发」。
        var rows = NmCalibrationTable.Build(
            [Rule("NM_05")],
            Report() with { IsPartial = true });

        var row = Assert.Single(rows);
        Assert.Equal(NmRuleStatus.Unmodeled, row.Status);
    }

    [Fact]
    public void UnobservableRule_IsUnmodeled_WithItsOwnCode()
    {
        var rule = Rule("NM_99") with { Observability = NmObservability.Unobservable };
        var rows = NmCalibrationTable.Build(
            [rule], Report(unmodeled: [Unmodeled("NM_99", NmUnmodeledCodes.Unobservable)]));

        var row = Assert.Single(rows);
        Assert.Equal(NmRuleStatus.Unmodeled, row.Status);
        Assert.Equal(NmUnmodeledCodes.Unobservable, row.ReasonCode);
    }

    // —— 身份与归因 ——

    [Fact]
    public void Row_CarriesClausesAndRequirement()
    {
        var rows = NmCalibrationTable.Build([Rule("NM_NID", "nid_id_mapping")], Report());

        var row = Assert.Single(rows);
        Assert.Equal(Clauses54, row.Clauses);
        Assert.Equal("req NM_NID", row.Requirement);
    }

    [Fact]
    public void RuleOrder_IsByRuleId()
    {
        // 表的顺序必须确定：否则同一份报告每次刷新行顺序都可能变。
        var rows = NmCalibrationTable.Build(
            [Rule("NM_Z"), Rule("NM_A"), Rule("NM_M")], Report());

        Assert.Equal(["NM_A", "NM_M", "NM_Z"], rows.Select(r => r.RuleId));
    }

    [Fact]
    public void FindingsWithoutRuleId_AreIgnored()
    {
        // M1 分析器产出的 finding 没有 RuleId（不属于任何企标规则）：
        // 它们不能凭空让某条规则显示为「触发」。
        var rows = NmCalibrationTable.Build(
            [Rule("NM_05")], Report(findings: [Finding(null)]));

        // 完整报告里该规则无三态记录 → 跑过且未违规（不是「未建模」）。
        Assert.Equal(NmRuleStatus.NotTriggered, Assert.Single(rows).Status);
    }

    [Fact]
    public void TriggeredRow_ExposesEvidenceTimesForSeek()
    {
        // spec §10.2：点击规则要能 seek 到证据时刻。
        var rows = NmCalibrationTable.Build(
            [Rule("NM_05")], Report(findings: [Finding("NM_05", at: 3.25)]));

        Assert.Equal([3.25], Assert.Single(rows).EvidenceTimes);
    }

    // —— 观测 vs 期望 ——

    [Fact]
    public void Calibration_ExposesCycleObservedVersusExpected()
    {
        var report = Report() with
        {
            Messages =
            [
                new NmMessageReport("0x500", false, "NM", "A", 20, 0, 10,
                    new NmCycleStats(520, 530, 500, 540, 20, 2, 19, 500, 50),
                    Array.Empty<NmSegment>()),
            ],
        };

        var comparisons = NmCalibrationTable.BuildComparisons(report);

        var cycle = Assert.Single(comparisons, c => c.Metric == NmCalibrationMetric.CycleMs);
        Assert.Equal("0x500", cycle.Subject);
        // 期望值带容差（500ms ±50ms）——面板要让用户看到判据，不只是目标值。
        Assert.Contains("500ms", cycle.Expected, StringComparison.Ordinal);
        Assert.Contains("±50ms", cycle.Expected, StringComparison.Ordinal);
        Assert.Contains("520", cycle.Observed, StringComparison.Ordinal);
        // 520 与 500 相差 20，在 ±50 容差内。
        Assert.True(cycle.WithinTolerance);
    }

    [Fact]
    public void Calibration_SkipsMessageWithoutExpectedCycle()
    {
        // 没有声明周期就没有「期望」：不能把中位数当成期望值自比自。
        var report = Report() with
        {
            Messages =
            [
                new NmMessageReport("0x500", false, "NM", "A", 20, 0, 10,
                    new NmCycleStats(520, 530, 500, 540, 20, 2, 19),
                    Array.Empty<NmSegment>()),
            ],
        };

        Assert.Empty(NmCalibrationTable.BuildComparisons(report));
    }

    [Fact]
    public void Calibration_SkipsInsufficientSamples()
    {
        // 有效间隔 < 5 时中位数不可用（NmCycleStats.InsufficientSamples），
        // 此时「观测 520ms」是伪精确——不得进对比表。
        var report = Report() with
        {
            Messages =
            [
                new NmMessageReport("0x500", false, "NM", "A", 2, 0, 10,
                    new NmCycleStats(null, null, null, null, null, 0, 1, 500, 50,
                        InsufficientSamples: true),
                    Array.Empty<NmSegment>()),
            ],
        };

        Assert.Empty(NmCalibrationTable.BuildComparisons(report));
    }

    // —— 数据体检（§9 三态）——

    [Fact]
    public void Calibration_ExposesDataQualityChecks()
    {
        var report = Report(checks:
        [
            new NmDataQualityCheck("unknown_id", NmDataQualityStatus.Passed,
                "全部帧 ID 已知", Array.Empty<NmTimeRange>(), Array.Empty<string>()),
            new NmDataQualityCheck("dlc_mismatch", NmDataQualityStatus.NotChecked,
                "无可比对帧", Array.Empty<NmTimeRange>(), Array.Empty<string>()),
        ]);

        var checks = NmCalibrationTable.BuildDataQuality(report);

        Assert.Equal(2, checks.Count);
        Assert.Equal(NmDataQualityStatus.Passed, checks[0].Status);
        Assert.Equal(NmDataQualityStatus.NotChecked, checks[1].Status);
    }

    [Fact]
    public void Calibration_NoChecks_ReturnsEmpty()
    {
        Assert.Empty(NmCalibrationTable.BuildDataQuality(Report()));
    }

    // —— 未建模条款（§10.2 显式列出，附条款号）——

    [Fact]
    public void Calibration_ExposesUnmodeledClausesWithNumbers()
    {
        var report = Report(unmodeled:
        [
            Unmodeled("NM_05"),
            new NmUnmodeledRule("§5.4.4", "条款不可观测",
                RequirementId: "NM_17", SourceRef: "§5.4.4")
            {
                RuleId = "NM_17",
                Clauses = ["§5.4.4", "§5.4.5"],
                ReasonCode = NmUnmodeledCodes.Unobservable,
            },
        ]);

        var clauses = NmCalibrationTable.BuildUnmodeledClauses(report);

        Assert.Equal(2, clauses.Count);
        // 多条款规则必须列出全部条款号，不能只报第一个。
        Assert.Contains(clauses, c => c.Clauses.Count == 2 && c.Clauses.Contains("§5.4.5"));
    }
}
