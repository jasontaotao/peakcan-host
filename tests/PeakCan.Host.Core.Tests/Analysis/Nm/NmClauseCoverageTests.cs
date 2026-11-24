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

    // —— 声明式「未覆盖半边」（spec §9.5 的 NM_03/awb 缺口）——

    /// <summary>
    /// <b>这是本组测试要消灭的静默通过</b>：<c>NM_BURST_SHAPE</c> 的 clauses 是
    /// <c>[NM_03, NM_04, NM_16]</c>，规则判了快发形态 → NM_03 整条显示「已覆盖」。
    /// 但 NM_03 还有 AWB=1 这半边，而 <c>bit_rule</c> 只支持 <c>is_zero</c>，
    /// AWB 从未被检查。用户看到「已覆盖」会以为 NM_03 验完了。
    /// <para>
    /// 修法不是把 NM_03 改判 NotCovered（快发半边确实验了），而是降为 Partial
    /// 并给出 <c>unimplemented_primitive</c>——「验了一部分，另一部分原语没实现」。
    /// </para>
    /// </summary>
    [Fact]
    public void DeclaredUncoveredFacet_DowngradesCoveredClauseToPartial()
    {
        var coverage = NmClauseCoverage.Build(
            [Rule("NM_BURST_SHAPE", "NM_03", "NM_04", "NM_16")],
            [Eval("NM_BURST_SHAPE", NmRuleVerdict.EvaluatedNoFinding)],
            [new NmUncoveredClauseDef("NM_03", "awb", NmUnmodeledCodes.UnimplementedPrimitive)]);

        var row = Assert.Single(coverage, c => c.Clause == "NM_03");
        Assert.Equal(NmClauseStatus.Partial, row.Status);
        Assert.Equal(NmUnmodeledCodes.UnimplementedPrimitive, row.ReasonCode);
        // 覆盖它的规则仍要列出来：读者得知道「哪条规则验了这半边」。
        Assert.Equal(["NM_BURST_SHAPE"], row.RuleIds);
    }

    /// <summary>
    /// 同一个声明不得波及别的条款：NM_04 没有 awb 半边，必须仍是 Covered。
    /// </summary>
    [Fact]
    public void DeclaredUncoveredFacet_DoesNotAffectOtherClauses()
    {
        var coverage = NmClauseCoverage.Build(
            [Rule("NM_BURST_SHAPE", "NM_03", "NM_04", "NM_16")],
            [Eval("NM_BURST_SHAPE", NmRuleVerdict.EvaluatedNoFinding)],
            [new NmUncoveredClauseDef("NM_03", "awb", NmUnmodeledCodes.UnimplementedPrimitive)]);

        Assert.Equal(NmClauseStatus.Covered, Assert.Single(coverage, c => c.Clause == "NM_04").Status);
        Assert.Equal(NmClauseStatus.Covered, Assert.Single(coverage, c => c.Clause == "NM_16").Status);
    }

    /// <summary>
    /// 声明一个**完全没有规则覆盖**的条款：必须产出一行 <c>NotCovered</c>。
    /// <para>
    /// 这是「目录里有、无规则覆盖」的最简形态。没有它，U 级条款
    /// （NM_01 / NM_05 / NM_08 等）在覆盖表里根本不存在——读者会以为
    /// 报告只统计了部分条款，而不是「这些条款本工具永远验不了」。
    /// </para>
    /// </summary>
    [Fact]
    public void DeclaredClauseWithNoRule_IsNotCovered()
    {
        var coverage = NmClauseCoverage.Build(
            [Rule("R1", "NM_03")],
            [Eval("R1", NmRuleVerdict.EvaluatedNoFinding)],
            [new NmUncoveredClauseDef("NM_01", null, NmUnmodeledCodes.Unobservable)]);

        var row = Assert.Single(coverage, c => c.Clause == "NM_01");
        Assert.Equal(NmClauseStatus.NotCovered, row.Status);
        Assert.Equal(NmUnmodeledCodes.Unobservable, row.ReasonCode);
        Assert.Empty(row.RuleIds);
    }

    /// <summary>
    /// 同一条款被多条规则覆盖、其中一条已判，同时又有未覆盖声明 →
    /// 仍降为 Partial。声明表达的是「还有一半没验」，与规则判了几条无关。
    /// </summary>
    [Fact]
    public void DeclaredUncoveredFacet_BeatsMultipleCoveringRules()
    {
        var coverage = NmClauseCoverage.Build(
            [Rule("R1", "NM_03"), Rule("R2", "NM_03")],
            [
                Eval("R1", NmRuleVerdict.EvaluatedNoFinding),
                Eval("R2", NmRuleVerdict.EvaluatedWithFinding),
            ],
            [new NmUncoveredClauseDef("NM_03", "awb", NmUnmodeledCodes.UnimplementedPrimitive)]);

        var row = Assert.Single(coverage, c => c.Clause == "NM_03");
        Assert.Equal(NmClauseStatus.Partial, row.Status);
        Assert.Equal(["R1", "R2"], row.RuleIds);
    }

    /// <summary>
    /// 未传声明时行为必须与旧实现逐字节一致——这是向后兼容的硬要求，
    /// 现有 13 条测试都走这条路径。
    /// </summary>
    [Fact]
    public void NoDeclarations_BehavesExactlyAsBefore()
    {
        var coverage = NmClauseCoverage.Build(
            [Rule("R1", "NM_03")], [Eval("R1", NmRuleVerdict.EvaluatedNoFinding)]);

        Assert.Equal(NmClauseStatus.Covered, Assert.Single(coverage).Status);
        Assert.Null(Assert.Single(coverage).ReasonCode);
    }

    /// <summary>空声明列表等价于不传：不能因为「传了个空集合」就改变行为。</summary>
    [Fact]
    public void EmptyDeclarationList_IsSameAsNull()
    {
        var withEmpty = NmClauseCoverage.Build(
            [Rule("R1", "NM_03")], [Eval("R1", NmRuleVerdict.EvaluatedNoFinding)], []);
        var withNull = NmClauseCoverage.Build(
            [Rule("R1", "NM_03")], [Eval("R1", NmRuleVerdict.EvaluatedNoFinding)], null);

        Assert.Equal(withNull.Select(c => c.Clause), withEmpty.Select(c => c.Clause));
        Assert.Equal(withNull.Select(c => c.Status), withEmpty.Select(c => c.Status));
    }

    // —— 条款号别名归一 ——

    /// <summary>
    /// <c>附录 A</c> 与 <c>附录A</c> 是同一条款（<see cref="NmClauseRegistry"/> 两种都收）。
    /// 聚合必须归一，否则同一条款在表里出现两行——一行 Covered、一行「未覆盖」，
    /// 读者会以为规范里有两个附录 A。
    /// </summary>
    [Fact]
    public void AliasSpellings_AreMergedIntoOneRow()
    {
        var coverage = NmClauseCoverage.Build(
            [Rule("R1", "附录 A"), Rule("R2", "附录A")],
            [
                Eval("R1", NmRuleVerdict.EvaluatedNoFinding),
                Eval("R2", NmRuleVerdict.EvaluatedNoFinding),
            ]);

        var row = Assert.Single(coverage);
        Assert.Equal(["R1", "R2"], row.RuleIds);
    }

    /// <summary>
    /// 规则写一种写法、声明写另一种时，声明必须命中同一条款并降级——
    /// 不归一的话声明落在一个独立的空行上，被声明的条款反而仍是 Covered。
    /// </summary>
    [Fact]
    public void AliasSpellings_DeclarationStillDowngrades()
    {
        var coverage = NmClauseCoverage.Build(
            [Rule("R1", "附录 A")],
            [Eval("R1", NmRuleVerdict.EvaluatedNoFinding)],
            [new NmUncoveredClauseDef("附录A", "gw", NmUnmodeledCodes.Unobservable)]);

        var row = Assert.Single(coverage);
        Assert.Equal(NmClauseStatus.Partial, row.Status);
        Assert.Equal(NmUnmodeledCodes.Unobservable, row.ReasonCode);
    }
}
