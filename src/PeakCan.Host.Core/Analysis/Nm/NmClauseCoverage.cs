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

/// <summary>
/// 声明「这条条款有一部分**已知没有被检查**」（spec §9.5 的 NM_03/awb 缺口）。
/// <para>
/// <b>要消灭的静默通过</b>：<c>NM_BURST_SHAPE</c> 的 <c>clauses</c> 是
/// <c>[NM_03, NM_04, NM_16]</c>，规则判了快发形态 → 覆盖表把 NM_03 整条显示
/// 「已覆盖」。但 NM_03 还有「AWB=1」这半边，而 <c>bit_rule</c> 只支持
/// <c>is_zero</c>，AWB 从未被检查。用户看到「已覆盖」会以为 NM_03 验完了。
/// </para>
/// <para>
/// <b>为什么不把 NM_03 直接判 NotCovered</b>：快发半边确实验了，判「未覆盖」
/// 是另一种失真。正确结论是 <see cref="NmClauseStatus.Partial"/>——验了一部分。
/// </para>
/// <para>
/// <b>为什么 <see cref="Facet"/> 不进条款号</b>：条款号有锚定正则
/// （<c>NMStandardValidator.ClauseIdPattern</c>，只认 <c>NM_01</c> / <c>§5.4.2</c> /
/// <c>附录A</c>），<c>NM_03/awb</c> 会被校验器直接拒绝。唯一键与展示条款号必须
/// 分离——这正是 spec §5.4 <c>NmClauseDef(Id, Clause)</c> 的模型。
/// 本轮只取它最小的那一半：**声明式**的未覆盖项，不建完整目录、不动
/// <c>rule.clauses</c> 的值域（spec §4.0 仍未裁决）。
/// </para>
/// </summary>
/// <param name="Clause">面向人的条款号，必须与规则的 <c>clauses</c> 写法一致才能聚合。</param>
/// <param name="Facet">未覆盖的那一半（如 <c>awb</c>）；null = 整条都没有规则覆盖。</param>
/// <param name="ReasonCode">为什么没覆盖，取自 <see cref="NmUnmodeledCodes"/>。</param>
public sealed record NmUncoveredClauseDef(
    string Clause,
    string? Facet,
    string ReasonCode);

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

    /// <summary>声明了未覆盖、却没给原因码时的兜底（调用方漏填，不静默当作已覆盖）。</summary>
    public const string UncoveredCode = "uncovered";

    /// <summary>
    /// 条款号归一：把语义相同的写法收敛成同一个键。
    /// <para>
    /// <b>为什么必须归一</b>：<see cref="NmClauseRegistry.KnownClauses"/> 刻意同时收
    /// <c>附录 A</c> 与 <c>附录A</c>（形状正则的空格可选，两种都合法）。聚合若按
    /// 原始字符串比较，规则写一种、声明写另一种就永远对不上——声明落在一个独立的
    /// 空行上，被声明的条款反而仍显示 Covered，而同一条款在表里出现两行。
    /// </para>
    /// <para>
    /// 只做「已知别名」的映射，不做通用的空白/大小写折叠：后者会让
    /// <c>NM_03</c> 与 <c>nm_03</c> 也合并，而小写写法根本不该被接受
    /// （白名单是 ordinal 比较）。归一的口径必须与白名单一致，否则是另一种不一致。
    /// </para>
    /// </summary>
    private static string NormalizeClause(string clause)
        => string.Equals(clause, "附录 A", StringComparison.Ordinal) ? "附录A" : clause;

    /// <summary>
    /// 构建条款覆盖表。<paramref name="rules"/> 决定「哪些条款被覆盖」，
    /// <paramref name="evaluations"/> 决定「覆盖得怎么样」，
    /// <paramref name="declarations"/> 声明「哪些已知半边没被任何规则检查」。
    /// <para>
    /// 输出按条款号 ordinal 排序：同一份报告每次刷新的行序必须一致。
    /// </para>
    /// </summary>
    public static IReadOnlyList<NmClauseCoverageRow> Build(
        IReadOnlyList<NmRuleDef> rules,
        IReadOnlyList<NmRuleEvaluation> evaluations,
        IReadOnlyList<NmUncoveredClauseDef>? declarations = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(evaluations);

        // 按条款号聚合声明：同一条款可能声明多个未覆盖半边（awb + 别的）。
        var declared = new Dictionary<string, List<NmUncoveredClauseDef>>(StringComparer.Ordinal);
        foreach (var decl in declarations ?? Array.Empty<NmUncoveredClauseDef>())
        {
            if (string.IsNullOrWhiteSpace(decl.Clause)) continue;
            var key = NormalizeClause(decl.Clause);
            if (!declared.TryGetValue(key, out var list))
            {
                list = [];
                declared[key] = list;
            }
            list.Add(decl);
        }

        var byRule = evaluations.ToDictionary(e => e.RuleId, StringComparer.Ordinal);

        // clause → 覆盖它的规则 ID 与各自的 verdict
        var byClause = new Dictionary<string, List<(string RuleId, NmRuleEvaluation? Eval)>>(
            StringComparer.Ordinal);

        foreach (var rule in rules)
        {
            foreach (var raw in rule.Clauses)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                // 归一后再入表：规则写「附录 A」、声明写「附录A」必须落同一行。
                var clause = NormalizeClause(raw);
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

        // 声明里出现、但没有任何规则覆盖的条款：必须产出一行 NotCovered。
        // 没有这一步，U 级条款（NM_01 / NM_05 / NM_08…）在覆盖表里根本不存在，
        // 读者会以为报告只统计了部分条款，而不是「这些条款本工具永远验不了」。
        foreach (var (clause, decls) in declared)
        {
            if (byClause.ContainsKey(clause)) continue;
            // 多条声明取 ordinal 第一个原因码：不依赖声明顺序，结果可复现。
            var code = decls.Select(d => d.ReasonCode)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Order(StringComparer.Ordinal)
                .FirstOrDefault() ?? UncoveredCode;
            rows.Add(new NmClauseCoverageRow(
                clause, NmClauseStatus.NotCovered, Array.Empty<string>(), code));
        }

        // 应用「已知未覆盖半边」：把 Covered 降为 Partial。
        // 放在最后一步，因为它只削减覆盖强度、从不提升——顺序与规则判定无关。
        //
        // 已经 Partial 的行也要覆盖原因码：声明表达的是**结构性**事实
        // （这半边本工具永远不验），而运行时原因码（如 not_applicable = 本条 trace
        // 不适用）会让读者以为换个窗口就好了。结构性的优先级更高——
        // 实测踩到：NM_03 的运行时码是 not_applicable，若只处理 Covered 行，
        // 声明的原因码永远不会出现在报告里。
        if (declared.Count > 0)
        {
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                // NotCovered 不处理：它已经是最强结论，且没有规则可回指。
                if (row.Status == NmClauseStatus.NotCovered) continue;
                if (!declared.TryGetValue(row.Clause, out var decls)) continue;

                // 多个未覆盖半边取 ordinal 第一个原因码，同上。
                var code = decls.Select(d => d.ReasonCode)
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Order(StringComparer.Ordinal)
                    .FirstOrDefault() ?? UncoveredCode;
                rows[i] = row with { Status = NmClauseStatus.Partial, ReasonCode = code };
            }
        }

        return rows.OrderBy(r => r.Clause, StringComparer.Ordinal).ToArray();
    }
}
