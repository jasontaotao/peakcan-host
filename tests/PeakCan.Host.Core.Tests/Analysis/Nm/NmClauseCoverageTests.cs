using PeakCan.Host.Core.Analysis.Nm;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// spec §5.5：条款覆盖三态（Covered / Partial / NotCovered）。
/// <para>
/// <b>为什么必须是三态而不是布尔</b>：「求值成功」不等于「这条 trace 上验证了」。
/// 一条规则可能因为本条数据不适用而什么都没判（NotApplicable），也可能只判了一部分
/// （Partial）。把它们压成一个「覆盖了」会让读者以为条款已验证。
/// </para>
/// <para>
/// <b>聚合不能「取最差」</b>：吉利的 <c>NM_RMS_DURATION_BURST</c> /
/// <c>NM_RMS_DURATION_NOBURST</c> 是一对 <c>with_burst</c> 变体，每条 trace 必然
/// 有一条不适用。取最差会让真实违规稳定降级成 Partial——§14.6 要求检出的两段
/// RMS 违规全部失效。
/// </para>
/// </summary>
public class NmClauseCoverageTests
{
    private static NmRuleEvaluation Eval(string ruleId, NmRuleVerdict verdict,
        string? reasonCode = null)
        => new(ruleId, verdict, reasonCode, Array.Empty<string>());

    private static NmRuleDef Rule(string id, params string[] clauses)
        => new(id, "req", "normal",
            new NmRuleWhen("bit_rule", Role: "X"),
            new NmRuleExpect("is_zero"), "CODE")
        {
            Clauses = clauses,
        };

    // —— 单规则三态 ——

    [Fact]
    public void SingleRuleEvaluatedNoFinding_ClauseIsCovered()
    {
        var coverage = NmClauseCoverage.Build(
            [Rule("R1", "NM_03")], [Eval("R1", NmRuleVerdict.EvaluatedNoFinding)]);

        var row = Assert.Single(coverage);
        Assert.Equal("NM_03", row.Clause);
        Assert.Equal(NmClauseStatus.Covered, row.Status);
    }

    [Fact]
    public void SingleRuleEvaluatedWithFinding_ClauseIsCovered()
    {
        // 有违规也是「验证过」——条款确实被判定了，只是结论是违规。
        var coverage = NmClauseCoverage.Build(
            [Rule("R1", "NM_03")], [Eval("R1", NmRuleVerdict.EvaluatedWithFinding)]);

        Assert.Equal(NmClauseStatus.Covered, Assert.Single(coverage).Status);
    }

    [Fact]
    public void SingleRuleUnmodeled_ClauseIsPartial()
    {
        // 用 missing_profile_data 而不是 unobservable：后者是 U 级条款，
        // 按 spec §5.5 情形 5 必须判 NotCovered（见 UnobservableRuleClause_*）。
        var coverage = NmClauseCoverage.Build(
            [Rule("R1", "NM_03")],
            [Eval("R1", NmRuleVerdict.Unmodeled, NmUnmodeledCodes.MissingProfileData)]);

        var row = Assert.Single(coverage);
        Assert.Equal(NmClauseStatus.Partial, row.Status);
        Assert.Equal(NmUnmodeledCodes.MissingProfileData, row.ReasonCode);
    }

    /// <summary>
    /// <c>NotApplicable</c> 不是「未覆盖」也不是「覆盖」——是「本条 trace 上不适用」。
    /// 唯一规则不适用时，该条款判 Partial 并给出专用原因码，让读者知道
    /// 「不是没配规则，是这次数据碰不到」。
    /// </summary>
    [Fact]
    public void SingleRuleNotApplicable_ClauseIsPartialWithDedicatedCode()
    {
        var coverage = NmClauseCoverage.Build(
            [Rule("R1", "NM_03")],
            [Eval("R1", NmRuleVerdict.NotApplicable, NmUnmodeledCodes.NotApplicable)]);

        var row = Assert.Single(coverage);
        Assert.Equal(NmClauseStatus.Partial, row.Status);
        Assert.Equal(NmClauseCoverage.AllRulesNotApplicableCode, row.ReasonCode);
    }

    // —— 多规则聚合 ——

    /// <summary>
    /// <b>关键场景</b>：一条违规、一条不适用 → 必须 Covered，不能降级。
    /// 真实 trace 的两段 RMS 就是这样：BURST 变体命中违规、NOBURST 变体不适用。
    /// 取最差会把它判成 Partial，§14.6 要求的检出全部失效。
    /// </summary>
    [Fact]
    public void OneViolationOneNotApplicable_IsCovered_NotDowngraded()
    {
        var coverage = NmClauseCoverage.Build(
            [Rule("R_BURST", "NM_06"), Rule("R_NOBURST", "NM_06")],
            [
                Eval("R_BURST", NmRuleVerdict.EvaluatedWithFinding),
                Eval("R_NOBURST", NmRuleVerdict.NotApplicable, NmUnmodeledCodes.NotApplicable),
            ]);

        var row = Assert.Single(coverage);
        Assert.Equal("NM_06", row.Clause);
        Assert.Equal(NmClauseStatus.Covered, row.Status);
    }

    [Fact]
    public void OnePassOneUnmodeled_IsCovered()
    {
        // 有一条规则真的判了 → 条款至少被验证过一次。
        var coverage = NmClauseCoverage.Build(
            [Rule("R1", "NM_03"), Rule("R2", "NM_03")],
            [
                Eval("R1", NmRuleVerdict.EvaluatedNoFinding),
                Eval("R2", NmRuleVerdict.Unmodeled, NmUnmodeledCodes.MissingProfileData),
            ]);

        Assert.Equal(NmClauseStatus.Covered, Assert.Single(coverage).Status);
    }

    [Fact]
    public void AllRulesUnmodeled_IsPartial()
    {
        var coverage = NmClauseCoverage.Build(
            [Rule("R1", "NM_03"), Rule("R2", "NM_03")],
            [
                Eval("R1", NmRuleVerdict.Unmodeled, NmUnmodeledCodes.MissingProfileData),
                Eval("R2", NmRuleVerdict.Unmodeled, NmUnmodeledCodes.UnimplementedPrimitive),
            ]);

        var row = Assert.Single(coverage);
        Assert.Equal(NmClauseStatus.Partial, row.Status);
        // 多个原因时取 ordinal 第一个，保证确定性（不依赖声明顺序）。
        Assert.Equal(
            new[] { NmUnmodeledCodes.MissingProfileData, NmUnmodeledCodes.UnimplementedPrimitive }
                .Order(StringComparer.Ordinal).First(),
            row.ReasonCode);
    }

    // —— 一条规则挂多条条款 ——

    [Fact]
    public void RuleWithMultipleClauses_CoversEachIndependently()
    {
        var coverage = NmClauseCoverage.Build(
            [Rule("R1", "NM_03", "NM_04", "NM_16")],
            [Eval("R1", NmRuleVerdict.EvaluatedNoFinding)]);

        Assert.Equal(3, coverage.Count);
        Assert.All(coverage, row => Assert.Equal(NmClauseStatus.Covered, row.Status));
        Assert.Equal(["NM_03", "NM_04", "NM_16"], coverage.Select(c => c.Clause));
    }

    /// <summary>
    /// 规则的 <c>Clauses</c> 为空 = 不属于条款空间（如 base 的 AUTOSAR 规则）。
    /// 它既不参与覆盖统计，也不能凭空造出一条空条款。
    /// </summary>
    [Fact]
    public void RuleWithoutClauses_DoesNotAppearInCoverage()
    {
        var coverage = NmClauseCoverage.Build(
            [Rule("NM_CYCLE")], [Eval("NM_CYCLE", NmRuleVerdict.EvaluatedNoFinding)]);

        Assert.Empty(coverage);
    }

    // —— 边界 ——

    [Fact]
    public void RuleWithoutEvaluation_IsNotSilentlyDropped()
    {
        // 规则存在但引擎没给状态：这本身是缺陷（每条规则都该有状态）。
        // 不能静默跳过——那会让该条款凭空从覆盖表消失。
        var coverage = NmClauseCoverage.Build([Rule("R1", "NM_03")], []);

        var row = Assert.Single(coverage);
        Assert.Equal(NmClauseStatus.Partial, row.Status);
        Assert.Equal(NmClauseCoverage.NoEvaluationCode, row.ReasonCode);
    }

    /// <summary>
    /// spec §5.5 情形 5：U 级条款即使有规则覆盖，也必须是
    /// <see cref="NmClauseStatus.NotCovered"/> + 原因码 <c>unobservable</c>。
    /// <para>
    /// U 级条款无论如何都不产 finding（§3），把它显示成「部分覆盖」会暗示
    /// 部分验证过——真相是**永远不会被验证**。原因码必须是 <c>unobservable</c>
    /// 而非 <c>uncovered</c>：两者在 §9.4 是不同码。
    /// </para>
    /// </summary>
    [Fact]
    public void UnobservableRuleClause_IsNotCovered_WithUnobservableCode()
    {
        var coverage = NmClauseCoverage.Build(
            [Rule("NM_RESUME_TO_RMS", "NM_02", "NM_10", "NM_15")],
            [Eval("NM_RESUME_TO_RMS", NmRuleVerdict.Unmodeled, NmUnmodeledCodes.Unobservable)]);

        Assert.Equal(3, coverage.Count);
        Assert.All(coverage, row =>
        {
            Assert.Equal(NmClauseStatus.NotCovered, row.Status);
            Assert.Equal(NmUnmodeledCodes.Unobservable, row.ReasonCode);
        });
    }

    /// <summary>
    /// 对照组：非 U 级的 Unmodeled（缺 profile 数据等）仍是 Partial——
    /// 那是「这次没判成」，将来数据齐了就能判，与「永远不可观测」不同。
    /// </summary>
    [Fact]
    public void MissingProfileDataClause_IsPartial_NotNotCovered()
    {
        var coverage = NmClauseCoverage.Build(
            [Rule("R1", "NM_03")],
            [Eval("R1", NmRuleVerdict.Unmodeled, NmUnmodeledCodes.MissingProfileData)]);

        var row = Assert.Single(coverage);
        Assert.Equal(NmClauseStatus.Partial, row.Status);
        Assert.Equal(NmUnmodeledCodes.MissingProfileData, row.ReasonCode);
    }

    [Fact]
    public void Coverage_IsSortedByClause()
    {
        var coverage = NmClauseCoverage.Build(
            [Rule("R1", "§5.5"), Rule("R2", "NM_03")],
            [
                Eval("R1", NmRuleVerdict.EvaluatedNoFinding),
                Eval("R2", NmRuleVerdict.EvaluatedNoFinding),
            ]);

        // 顺序必须确定：否则同一份报告每次刷新行序都可能变。
        Assert.Equal(["NM_03", "§5.5"], coverage.Select(c => c.Clause));
    }
}
