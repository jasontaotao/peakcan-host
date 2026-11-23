using System.Diagnostics;

namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>规则求值输入（spec §7.1）。</summary>
public sealed record NmRuleContext(
    IReadOnlyList<NmFrame> NmFrames,
    IReadOnlyList<NmEpisode> Episodes,
    NmProfile Profile,
    // 持全局时序而非单个 NmEffectiveTiming：规则是跨报文求值的，
    // 传单个生效时序会让多报文场景用错周期（见计划 Interfaces 的偏差说明）。
    NmGlobalTiming Timing,
    NmWindow Window)
{
    /// <summary>待求值的规则；由调用方从展平 profile 取。</summary>
    public IReadOnlyList<NmRuleDef> Rules { get; init; } = Array.Empty<NmRuleDef>();

    /// <summary>该报文的生效时序（含报文级覆盖，spec §5.2）。</summary>
    public NmEffectiveTiming Resolve(NmMessageDef def) => NmTiming.Resolve(def, Timing);
}

/// <summary>规则求值结果（spec §7.1）。违规与事实严格分开。</summary>
public sealed record NmRuleResult(
    IReadOnlyList<NmFinding> Findings,
    IReadOnlyList<NmObservation> Observations,
    IReadOnlyList<NmUnmodeledRule> Unmodeled,
    IReadOnlyList<NmTruncation> Truncations);

/// <summary>
/// 未建模原因码（spec §9.4）。<c>missing_profile_data</c> 会让 AI 让用户去改 profile；
/// 若真实原因是「这条 trace 没有该事件」，那是把用户引向错误的方向。
/// </summary>
public static class NmUnmodeledCodes
{
    /// <summary>本条 trace 的帧里没有该规则要判的事件（静默段、停发段、RMS 锚点等）。</summary>
    public const string NotApplicable = "not_applicable";

    /// <summary>原语的 expect / 参数变体尚未实现。</summary>
    public const string UnimplementedPrimitive = "unimplemented_primitive";

    /// <summary>profile 缺少规则所需的声明（位角色、nid_byte、时限等）。</summary>
    public const string MissingProfileData = "missing_profile_data";

    /// <summary>U 级条款：规则本身不可观测。</summary>
    public const string Unobservable = "unobservable";
}

/// <summary>
/// 规则引擎（spec §7.2 闭集原语）。未知原语进 Unmodeled，不产 finding、不抛异常；
/// U 级条款无论如何都不产 finding（spec §3）。
/// </summary>
public static class NmRuleEngine
{
    public static NmRuleResult Evaluate(IReadOnlyList<NmRuleDef> rules, NmRuleContext context)
    {
        var findings = new List<NmFinding>();
        var observations = new List<NmObservation>();
        var unmodeled = new List<NmUnmodeledRule>();

        foreach (var rule in rules)
        {
            // spec §4.0：Clause 是条款号，不是规则名。规则身份由 RuleId 承载。
            // 取 ordinal 排序后的第一个，与 finding 回填的 RequirementId 同口径，
            // 避免同一规则的两个字段对「首条款」给出不同答案（不依赖 YAML 声明顺序）。
            var firstClause = rule.Clauses.Count > 0
                ? rule.Clauses.Order(StringComparer.Ordinal).First()
                : null;

            // spec §3：不可观测条款只能进 Unmodeled。
            if (rule.Observability == NmObservability.Unobservable)
            {
                unmodeled.Add(new NmUnmodeledRule(
                    firstClause ?? "", "条款不可观测（U 级），只能推断，不产 finding",
                    RequirementId: firstClause, SourceRef: rule.SourceRef)
                {
                    RuleId = rule.Id,
                    Clauses = rule.Clauses,
                    ReasonCode = NmUnmodeledCodes.Unobservable,
                });
                continue;
            }

            var implemented = NmRuleEvaluators.TryEvaluate(
                rule, context, findings, observations,
                out var unmodeledReason, out var unmodeledCode);

            if (!implemented)
            {
                unmodeled.Add(new NmUnmodeledRule(
                    firstClause ?? "", $"原语「{rule.When.Kind}」在 v1 闭集中无实现",
                    RequirementId: firstClause, SourceRef: rule.SourceRef)
                {
                    RuleId = rule.Id,
                    Clauses = rule.Clauses,
                    ReasonCode = NmUnmodeledCodes.UnimplementedPrimitive,
                });
            }
            else if (unmodeledReason is not null)
            {
                // 原语已实现，但本条规则缺参数 / 缺 profile 数据 / 期望未实现：
                // 必须显式进 Unmodeled，否则用户会把「没检查」误读成「通过了」。
                // 原因码由 evaluator 给出——「trace 里没有该事件」与「profile 缺数据」
                // 必须可区分，否则 AI 会让用户去改一份本来没问题的 profile。
                Debug.Assert(unmodeledCode is not null,
                    $"规则 {rule.Id} 给出了 unmodeledReason 却没有 unmodeledCode");
                unmodeled.Add(new NmUnmodeledRule(
                    firstClause ?? "", unmodeledReason,
                    RequirementId: firstClause, SourceRef: rule.SourceRef)
                {
                    RuleId = rule.Id,
                    Clauses = rule.Clauses,
                    ReasonCode = unmodeledCode ?? NmUnmodeledCodes.MissingProfileData,
                });
            }
        }

        // spec §4.0：条款归因由引擎统一回填，不依赖各 evaluator 记得填。
        // 9 处 finding 构造分散在三个文件，人工维护迟早漏。
        // 前置条件：规则 ID 唯一（NmStandardValidator 在加载期已保证）；
        // 重复 ID 在这里会抛 ArgumentException，而不是静默择一。
        var byRule = rules.ToDictionary(r => r.Id, StringComparer.Ordinal);
        for (var i = 0; i < findings.Count; i++)
        {
            if (findings[i].RuleId is not { } rid || !byRule.TryGetValue(rid, out var def)) continue;
            findings[i] = findings[i] with
            {
                Clauses = def.Clauses,
                // 兼容字段取确定性值：按 ordinal 排序后的第一个，不依赖 YAML 声明顺序。
                RequirementId = def.Clauses.Count == 0
                    ? null
                    : def.Clauses.Order(StringComparer.Ordinal).First(),
            };
        }

        return new NmRuleResult(findings, observations, unmodeled,
            Array.Empty<NmTruncation>());
    }
}
