using System.Globalization;
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
    IReadOnlyList<NmTruncation> Truncations)
{
    /// <summary>
    /// 逐规则求值状态（spec §7.1.1）。每条规则恰好一项，顺序与输入规则一致。
    /// <para>
    /// <b>为什么必须有它</b>：合规规则**零输出**——<c>BitRule</c> / <c>NidIdMapping</c> /
    /// <c>LayoutCheck</c> / <c>CycleTolerance</c> / <c>BurstShape</c> 在合法场景下
    /// <c>return true</c> 就结束，既不产 finding 也不产 observation。
    /// 按「findings/observations 是否非空」反推覆盖，会让**真实合规且充分求值的规则
    /// 永远不是 Covered**（spec §5.5）。
    /// </para>
    /// </summary>
    public IReadOnlyList<NmRuleEvaluation> Evaluations { get; init; }
        = Array.Empty<NmRuleEvaluation>();
}

/// <summary>
/// 一条规则的求值结论（spec §7.1.1）。
/// <para>
/// <b>与 <see cref="NmRuleStatus"/> 的区别</b>：那个是标定面板的**呈现**三态
/// （触发/未触发/未建模），由报告反推；本枚举是引擎的**求值**结论，
/// 由求值过程直接产出。两者名称相近但语义不同，故分开命名。
/// </para>
/// </summary>
public enum NmRuleVerdict
{
    /// <summary>求值成功、未发现违规。这是最常见的合法情形，也是零输出的那些。</summary>
    EvaluatedNoFinding = 0,

    /// <summary>求值成功、产出了 finding。</summary>
    EvaluatedWithFinding = 1,

    /// <summary>部分报文/段有结论，其余不可判（同一规则既覆盖又未覆盖）。</summary>
    Partial = 2,

    /// <summary>本条 trace 上不适用（如 with_burst 不匹配、没有可分析的 episode）。</summary>
    NotApplicable = 3,

    /// <summary>没给出可判定的结论；原因码见 <see cref="NmRuleEvaluation.ReasonCode"/>。</summary>
    Unmodeled = 4,
}

/// <summary>一条规则的求值结论与证据（spec §7.1.1）。</summary>
public sealed record NmRuleEvaluation(
    string RuleId,
    NmRuleVerdict Status,
    // 与 NmUnmodeledRule.ReasonCode 同一词表（NmUnmodeledCodes）。
    string? ReasonCode,
    // 产出的 finding/observation 标识，供报告回溯到具体证据。
    IReadOnlyList<string> EvidenceRefs);

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

    /// <summary>
    /// 规则部分成功：已确证的部分照常产出结论，但有一部分因**数据本身**不可判
    /// （如某帧 DLC 不足读不出状态位）而未判定。
    /// <para>
    /// 与 <see cref="MissingProfileData"/> 的区别是根因归属：这里 profile 声明齐全，
    /// 缺的是 trace 里的字节。用错码会让 AI 指引用户去改一份本来没问题的 profile。
    /// </para>
    /// </summary>
    public const string PartialEvidence = "partial_evidence";

    /// <summary>
    /// 引擎内部不一致：evaluator 给出了原因却没有给出原因码。
    /// <para>
    /// 这是**代码缺陷**，不是 profile 问题，也不是数据问题，因此不能复用
    /// <see cref="MissingProfileData"/>——那会让 AI 指引用户去改一份本来没问题的 profile。
    /// 生产路径上 evaluator 全部成对赋值（原因与码同时给），本码只在实现被改坏时出现。
    /// </para>
    /// <para>
    /// 之所以必须有这个码：原实现用 <c>unmodeledCode ?? MissingProfileData</c> 兜底，
    /// 而 <c>Debug.Assert</c> 在 Release 下整句消失——两条合起来的效果是
    /// 「Release 下把引擎缺陷伪装成 profile 缺数据」，正是本仓库禁止的静默降级。
    /// </para>
    /// </summary>
    public const string InternalInconsistent = "internal_inconsistent";
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
        var evaluations = new List<NmRuleEvaluation>(rules.Count);

        foreach (var rule in rules)
        {
            // spec §4.0：Clause 是条款号，不是规则名。规则身份由 RuleId 承载。
            // 取 ordinal 排序后的第一个，与 finding 回填的 RequirementId 同口径，
            // 避免同一规则的两个字段对「首条款」给出不同答案（不依赖 YAML 声明顺序）。
            var firstClause = rule.Clauses.Count > 0
                ? rule.Clauses.Order(StringComparer.Ordinal).First()
                : null;

            // 记录本规则开始前的产出数，用增量判断它产了什么。
            // 不能靠「规则跑完后列表非空」——前一条规则的产出会污染后一条。
            var findingsBefore = findings.Count;
            var observationsBefore = observations.Count;

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
                evaluations.Add(new NmRuleEvaluation(
                    rule.Id, NmRuleVerdict.Unmodeled, NmUnmodeledCodes.Unobservable,
                    Array.Empty<string>()));
                continue;
            }

            var implemented = NmRuleEvaluators.TryEvaluateWithJudgement(
                rule, context, findings, observations,
                out var unmodeledReason, out var unmodeledCode, out var judged);

            var producedFindings = findings.Count - findingsBefore;
            var producedObservations = observations.Count - observationsBefore;

            // 「本规则是否真的判过至少一个样本」由原语显式给出，**不能靠观测种类反推**：
            // repeat_message_duration 对每一段都先无条件产事实观测、再在形态不匹配时
            // 追加 _not_applicable，所以「有 not_applicable」必然「也有事实观测」——
            // 按种类推断会让「全部段被跳过」永远得不出 NotApplicable。
            var notApplicableOnly = !judged && producedFindings == 0;

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
                evaluations.Add(new NmRuleEvaluation(
                    rule.Id, NmRuleVerdict.Unmodeled, NmUnmodeledCodes.UnimplementedPrimitive,
                    EvidenceRefs(findings, observations, findingsBefore, observationsBefore)));
            }
            else if (unmodeledReason is not null)
            {
                // 原语已实现，但本条规则缺参数 / 缺 profile 数据 / 期望未实现：
                // 必须显式进 Unmodeled，否则用户会把「没检查」误读成「通过了」。
                // 原因码由 evaluator 给出——「trace 里没有该事件」与「profile 缺数据」
                // 必须可区分，否则 AI 会让用户去改一份本来没问题的 profile。
                //
                // 断言是开发期护栏：Release 下整句消失，所以兜底原因码不能依赖它
                // （见 BuildEvaluatedUnmodeled 的注释）。
                Debug.Assert(unmodeledCode is not null,
                    $"规则 {rule.Id} 给出了 unmodeledReason 却没有 unmodeledCode");
                unmodeled.Add(BuildEvaluatedUnmodeled(
                    rule, firstClause, unmodeledReason, unmodeledCode));

                // 「已实现但本条不可求值」有两种性质，必须分开：
                //  - 同时产出了 finding/observation：部分成功 → Partial
                //    （RepeatMessageDuration 按报文收集 reasons，典型场景）
                //  - 什么都没产：整条不适用 → Unmodeled
                // 把部分成功报成 Unmodeled 会丢掉已确证的结论；反过来会把
                // 「没判成」报成「判过了」。
                var verdict = producedFindings > 0 || (producedObservations > 0 && !notApplicableOnly)
                    ? NmRuleVerdict.Partial
                    : NmRuleVerdict.Unmodeled;
                evaluations.Add(new NmRuleEvaluation(
                    rule.Id, verdict, unmodeledCode ?? NmUnmodeledCodes.InternalInconsistent,
                    EvidenceRefs(findings, observations, findingsBefore, observationsBefore)));
            }
            else if (notApplicableOnly)
            {
                // 原语明确表示「一段都没判」（形态全被 with_burst 选择器跳过、
                // 或全部段无法定界）：本条 trace 上不适用。
                // 既不是「合规」也不是「未建模」——是「这次碰不到」。
                // 判成 EvaluatedNoFinding 会让条款覆盖把该条款算成 Covered。
                evaluations.Add(new NmRuleEvaluation(
                    rule.Id, NmRuleVerdict.NotApplicable, NmUnmodeledCodes.NotApplicable,
                    EvidenceRefs(findings, observations, findingsBefore, observationsBefore)));
            }
            else
            {
                // 求值成功。有 finding 就是违规，否则是「跑过了、没违规」——
                // 后者正是那些零输出的合规规则，绝不能因为「没输出」就当没跑。
                evaluations.Add(new NmRuleEvaluation(
                    rule.Id,
                    producedFindings > 0
                        ? NmRuleVerdict.EvaluatedWithFinding
                        : NmRuleVerdict.EvaluatedNoFinding,
                    null,
                    EvidenceRefs(findings, observations, findingsBefore, observationsBefore)));
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
            Array.Empty<NmTruncation>())
        {
            Evaluations = evaluations,
        };
    }

    /// <summary>
    /// 取本规则新增的 finding/observation 标识。
    /// <para>
    /// <b>序号是「本规则内第几条」，不是全局下标。</b>全局下标在报告里对不上：
    /// <c>NmAnalyzer</c> 在调用引擎前已加了 trace 边界 / 空档 / 内容 finding，
    /// 引擎结果之后才合并；去重还会再改偏移。拿全局下标当 <c>report.Findings</c>
    /// 的索引会指向完全无关的条目，而这个字符串是发给 AI 的——AI 会照着去对照。
    /// </para>
    /// <para>
    /// 同一规则可能产多条同码 finding（真实 trace 的两段 RMS 都产
    /// <c>NM_REPEAT_MESSAGE_DURATION_EXCEEDED</c>），所以序号必须保留。
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> EvidenceRefs(
        List<NmFinding> findings, List<NmObservation> observations,
        int findingsBefore, int observationsBefore)
    {
        var count = findings.Count - findingsBefore + observations.Count - observationsBefore;
        if (count == 0) return Array.Empty<string>();

        var refs = new List<string>(count);
        for (var i = findingsBefore; i < findings.Count; i++)
        {
            refs.Add(string.Create(CultureInfo.InvariantCulture,
                $"finding:{findings[i].Code}#{i - findingsBefore}"));
        }
        for (var i = observationsBefore; i < observations.Count; i++)
        {
            refs.Add(string.Create(CultureInfo.InvariantCulture,
                $"observation:{observations[i].Kind}#{i - observationsBefore}"));
        }
        return refs;
    }

    /// <summary>
    /// 构造「原语已实现、但本条规则无法求值」的 Unmodeled 条目。
    /// <para>
    /// 独立成方法是为了让「evaluator 给了原因却没给原因码」这条分支**可被测试直接覆盖**：
    /// 生产 evaluator 的每个 reason 都成对给出 code，从公开入口
    /// 造不出缺码的规则，只靠断言等于把这条分支排除在测试之外。
    /// </para>
    /// <para>
    /// 缺码是**代码缺陷**，不是 profile 问题：Debug 下由调用点的断言暴露，Release 下
    /// 断言整句消失，所以这里必须给出一个运行期可见的独立原因码。若退回
    /// <see cref="NmUnmodeledCodes.MissingProfileData"/> 兜底，Release 报告会指控
    /// 一份本来没问题的 profile——这正是本仓库禁止的静默降级。
    /// </para>
    /// <para>
    /// 断言**不放在本方法内**：测试宿主会把 <c>Debug.Assert</c> 转成异常，放在这里
    /// 会让这条分支永远测不到。断言属于调用点的开发期护栏，本方法只负责映射。
    /// </para>
    /// </summary>
    internal static NmUnmodeledRule BuildEvaluatedUnmodeled(
        NmRuleDef rule, string? firstClause, string unmodeledReason, string? unmodeledCode)
        => new(
            firstClause ?? "",
            unmodeledCode is null
                ? unmodeledReason + "（引擎缺陷：未给出原因码，请报 issue）"
                : unmodeledReason,
            RequirementId: firstClause, SourceRef: rule.SourceRef)
        {
            RuleId = rule.Id,
            Clauses = rule.Clauses,
            ReasonCode = unmodeledCode ?? NmUnmodeledCodes.InternalInconsistent,
        };
}
