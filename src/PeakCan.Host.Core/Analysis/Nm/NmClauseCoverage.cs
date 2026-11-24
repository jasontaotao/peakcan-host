namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>条款在本次分析里的覆盖状态（spec §5.5）。</summary>
public enum NmClauseStatus
{
    /// <summary>有规则覆盖该条款，且至少一条判出了结论（合规或违规）。</summary>
    Covered = 0,

    /// <summary>覆盖该条款的规则都没给出结论（未建模 / 全部不适用）。</summary>
    Partial = 1,

    /// <summary>没有任何规则覆盖该条款。</summary>
    NotCovered = 2,
}

/// <summary>一条条款的覆盖结论（spec §5.5）。</summary>
public sealed record NmClauseCoverageRow(
    string Clause,
    NmClauseStatus Status,
    // 覆盖该条款的规则 ID，按 ordinal 排序——读者要能顺着它回到具体规则。
    IReadOnlyList<string> RuleIds,
    // 未覆盖/部分覆盖时的原因码；覆盖完整时为 null。
    string? ReasonCode)
{
    /// <summary>面板与导出显示用的中文状态。</summary>
    public string StatusText => Status switch
    {
        NmClauseStatus.Covered => "已覆盖",
        NmClauseStatus.Partial => "部分覆盖",
        NmClauseStatus.NotCovered => "未覆盖",
        // 显式列全：新增枚举值时编译期暴露，不静默归一成「未覆盖」。
        _ => throw new InvalidOperationException(
            $"未映射的条款覆盖状态：{Status}"),
    };
}

/// <summary>
/// 条款覆盖统计（spec §5.5）。
/// <para>
/// <b>为什么不能从 findings/observations 反推</b>：合规规则**零输出**——
/// <c>BitRule</c> / <c>LayoutCheck</c> 等在合法场景 <c>return true</c> 就结束。
/// 按「有输出才算覆盖」判定，真实合规且充分求值的规则反而永远不是 Covered。
/// 覆盖必须由 <see cref="NmRuleEvaluation"/> 的 verdict 判定。
/// </para>
/// <para>
/// <b>聚合不取最差</b>：吉利 <c>NM_RMS_DURATION_BURST</c> / <c>NM_RMS_DURATION_NOBURST</c>
/// 是一对 <c>with_burst</c> 变体，每条 trace 必然有一条 <c>NotApplicable</c>。
/// 取最差会让真实违规稳定降级成 Partial。
/// </para>
/// <para>
/// <b>范围说明</b>：本类只统计「有规则覆盖的条款」。profile 目前没有独立的条款目录
/// （<c>NmClauseDef</c>），所以「目录里有但无规则覆盖」的 <see cref="NmClauseStatus.NotCovered"/>
/// 暂时不会产出——那需要条款目录全链路（spec §19.5 记录为未闭合）。
/// </para>
/// </summary>
public static class NmClauseCoverage
{
    /// <summary>覆盖该条款的规则全部 <c>NotApplicable</c>（本条 trace 碰不到）。</summary>
    public const string AllRulesNotApplicableCode = "all_rules_not_applicable";

    /// <summary>规则存在但引擎没给求值状态——引擎缺陷，不是数据问题。</summary>
    public const string NoEvaluationCode = "internal_inconsistent";

    /// <summary>
    /// 构建条款覆盖表。<paramref name="rules"/> 决定「哪些条款被覆盖」，
    /// <paramref name="evaluations"/> 决定「覆盖得怎么样」。
    /// <para>
    /// 输出按条款号 ordinal 排序：同一份报告每次刷新的行序必须一致。
    /// </para>
    /// </summary>
    public static IReadOnlyList<NmClauseCoverageRow> Build(
        IReadOnlyList<NmRuleDef> rules,
        IReadOnlyList<NmRuleEvaluation> evaluations)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(evaluations);

        var byRule = evaluations.ToDictionary(e => e.RuleId, StringComparer.Ordinal);

        // clause → 覆盖它的规则 ID 与各自的 verdict
        var byClause = new Dictionary<string, List<(string RuleId, NmRuleEvaluation? Eval)>>(
            StringComparer.Ordinal);

        foreach (var rule in rules)
        {
            foreach (var clause in rule.Clauses)
            {
                if (string.IsNullOrWhiteSpace(clause)) continue;
                if (!byClause.TryGetValue(clause, out var list))
                {
                    list = [];
                    byClause[clause] = list;
                }
                byRule.TryGetValue(rule.Id, out var eval);
                list.Add((rule.Id, eval));
            }
        }

        var rows = new List<NmClauseCoverageRow>(byClause.Count);
        foreach (var (clause, entries) in byClause)
        {
            var ruleIds = entries.Select(e => e.RuleId)
                .Order(StringComparer.Ordinal).ToArray();

            // 剔除 NotApplicable：它不代表任何验证结论。
            var applicable = entries.Where(e => e.Eval is not null
                && e.Eval.Status != NmRuleVerdict.NotApplicable).ToArray();

            NmClauseStatus status;
            string? reasonCode;

            if (applicable.Any(e => e.Eval!.Status is NmRuleVerdict.EvaluatedNoFinding
                    or NmRuleVerdict.EvaluatedWithFinding))
            {
                // 至少一条规则真的判了 → 条款已验证过。
                status = NmClauseStatus.Covered;
                reasonCode = null;
            }
            else if (applicable.Length > 0)
            {
                // 只剩 Unmodeled / Partial：判过但没结论。
                //
                // 其中「U 级不可观测」必须单独判 NotCovered（spec §5.5 情形 5）：
                // U 级条款无论如何都不产 finding（§3），把它显示成「部分覆盖」会
                // 暗示部分验证过——真相是**永远不会被验证**。原因码必须是
                // unobservable 而不是 uncovered：两者在 §9.4 是不同码。
                //
                // 这一支不依赖条款目录，规则本身带 Observability 就够了。
                var codes = applicable
                    .Select(e => e.Eval!.ReasonCode ?? NoEvaluationCode)
                    .ToArray();

                if (codes.All(c => string.Equals(
                        c, NmUnmodeledCodes.Unobservable, StringComparison.Ordinal)))
                {
                    status = NmClauseStatus.NotCovered;
                    reasonCode = NmUnmodeledCodes.Unobservable;
                }
                else
                {
                    status = NmClauseStatus.Partial;
                    // 多个原因取 ordinal 第一个：不依赖声明顺序，结果可复现。
                    reasonCode = codes.Order(StringComparer.Ordinal).First();
                }
            }
            else if (entries.All(e => e.Eval is not null))
            {
                // 全部规则都给了状态，且全是 NotApplicable。
                status = NmClauseStatus.Partial;
                reasonCode = AllRulesNotApplicableCode;
            }
            else
            {
                // 有规则没拿到状态——引擎缺陷。不能静默跳过，否则该条款
                // 会从覆盖表凭空消失，读者以为 profile 没配规则。
                status = NmClauseStatus.Partial;
                reasonCode = NoEvaluationCode;
            }

            rows.Add(new NmClauseCoverageRow(clause, status, ruleIds, reasonCode));
        }

        return rows.OrderBy(r => r.Clause, StringComparer.Ordinal).ToArray();
    }
}
