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
            // spec §3：不可观测条款只能进 Unmodeled。
            if (rule.Observability == NmObservability.Unobservable)
            {
                unmodeled.Add(new NmUnmodeledRule(
                    rule.Id, "条款不可观测（U 级），只能推断，不产 finding",
                    RequirementId: rule.Id, SourceRef: rule.SourceRef));
                continue;
            }

            var implemented = NmRuleEvaluators.TryEvaluate(
                rule, context, findings, observations, out var unmodeledReason);

            if (!implemented)
            {
                unmodeled.Add(new NmUnmodeledRule(
                    rule.Id, $"原语「{rule.When.Kind}」在 v1 闭集中无实现",
                    RequirementId: rule.Id, SourceRef: rule.SourceRef));
            }
            else if (unmodeledReason is not null)
            {
                // 原语已实现，但本条规则缺参数 / 缺 profile 数据 / 期望未实现：
                // 必须显式进 Unmodeled，否则用户会把「没检查」误读成「通过了」。
                unmodeled.Add(new NmUnmodeledRule(
                    rule.Id, unmodeledReason,
                    RequirementId: rule.Id, SourceRef: rule.SourceRef));
            }
        }

        return new NmRuleResult(findings, observations, unmodeled,
            Array.Empty<NmTruncation>());
    }
}
