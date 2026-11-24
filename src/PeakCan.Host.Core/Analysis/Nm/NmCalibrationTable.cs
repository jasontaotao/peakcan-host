using System.Globalization;
using System.Text.RegularExpressions;

namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>规则在本次分析里的结论（spec §10.2 标定面板）。</summary>
public enum NmRuleStatus
{
    /// <summary>有该 RuleId 的 finding：规则判定违规。</summary>
    Triggered = 0,

    /// <summary>
    /// 规则跑了、检查了、没发现违规。
    /// <para>
    /// 两条来源：引擎 verdict 为 <see cref="NmRuleVerdict.EvaluatedNoFinding"/>
    /// （含零输出的合规规则），或回退路径下有观测无 finding。
    /// </para>
    /// </summary>
    NotTriggered = 1,

    /// <summary>规则没给出可判定的结论（原因码见 <see cref="NmCalibrationRow.ReasonCode"/>）。</summary>
    Unmodeled = 2,

    /// <summary>
    /// 规则已实现、但本次 trace 上碰不到它要判的事件（引擎
    /// <see cref="NmRuleVerdict.NotApplicable"/>）。
    /// <para>
    /// <b>必须与 <see cref="NotTriggered"/> 分开</b>：引擎特意为这个区别单独设了一个
    /// verdict，并注明「判成 EvaluatedNoFinding 会让条款覆盖把该条款算成 Covered」。
    /// 展示层若把它显示成「未触发」，用户会读作「查过了没问题」——
    /// 把「这次没碰到」报成「查过且合规」是标定面板最不能出的错之一。
    /// </para>
    /// </summary>
    NotApplicable = 3,
}

/// <summary>标定面板的一行：一条规则在本次分析中的结论。</summary>
public sealed record NmCalibrationRow(
    string RuleId,
    string Requirement,
    NmRuleStatus Status,
    IReadOnlyList<string> Clauses,
    IReadOnlyList<string> Messages,
    IReadOnlyList<double> EvidenceTimes,
    string? ReasonCode,
    string? Reason)
{
    /// <summary>面板显示用的中文状态（spec §10.2）。</summary>
    public string StatusText => Status switch
    {
        NmRuleStatus.Triggered => "触发",
        NmRuleStatus.NotTriggered => "未触发",
        NmRuleStatus.NotApplicable => "不适用",
        _ => "未建模",
    };

    /// <summary>
    /// 引擎的原始求值结论；null = 报告里没有逐规则状态（旧报告 / 未选中企标），
    /// 本行是由 finding/observation 反推出来的。
    /// <para>
    /// 保留它而不是只留三态：<see cref="NmRuleVerdict.Partial"/> 与
    /// <see cref="NmRuleVerdict.Unmodeled"/> 都映射到 <see cref="NmRuleStatus.Unmodeled"/>，
    /// UI 要靠原始 verdict 说清到底是哪一种。
    /// </para>
    /// </summary>
    public NmRuleVerdict? Verdict { get; init; }

    /// <summary>该规则产出的观测条数（用于「未触发」时说清「基于几条观测」）。</summary>
    public int ObservationCount { get; init; }

    /// <summary>
    /// 规则的**结构身份**：原语种类 + 期望种类 + finding 码。
    /// <para>
    /// 用于把同质规则折叠成一组（吉利 13 条预留位检查占掉半屏）。必须是结构事实，
    /// <b>不能用 RuleId 前缀猜</b>——ID 是自由文本，改个名字就会让折叠行为漂移。
    /// </para>
    /// <para>空串 = 未提供（手工构造的行），这类行永不参与折叠。</para>
    /// </summary>
    public string StructuralKey { get; init; } = "";

    /// <summary>
    /// 行的主标题：规则的中文含义。
    /// <para>
    /// 与 <see cref="RuleId"/> 分开：ID 是身份（<c>NM_RESERVED_B1_1</c>），
    /// 这句话才是用户想知道的「这条在查什么」。原实现只把 Requirement 放在
    /// ToolTip 里，用户必须逐行 hover 才能看懂表。
    /// </para>
    /// </summary>
    public string Title => string.IsNullOrWhiteSpace(Requirement) ? RuleId : Requirement;

    /// <summary>
    /// 行的完整提示文本：规则 ID + 完整含义 + 条款号。
    /// <para>
    /// 正文里的 <see cref="Title"/> 会被省略号截断（360px 面板下长句必然截），
    /// 截掉的尾部常常正是条款号。ToolTip 必须能给出全文，否则用户无处可看。
    /// </para>
    /// </summary>
    public string ToolTipText
    {
        get
        {
            var parts = new List<string>(3) { RuleId };
            if (!string.IsNullOrWhiteSpace(Requirement)) parts.Add(Requirement);
            if (Clauses.Count > 0) parts.Add(string.Join(" / ", Clauses));
            return string.Join("\n", parts);
        }
    }

    /// <summary>
    /// 行的一句话说明：说清「是什么」或「凭什么」。
    /// <para>
    /// <b>为什么必须有它</b>：原实现只在「未建模」时给 <see cref="Reason"/>，
    /// 触发行在界面上除规则 ID 外一个字都没有——而 finding 原文一直躺在
    /// <see cref="Messages"/> 里没人渲染。同理，「未触发」不给依据时，
    /// 用户无法区分「查过没问题」与「压根没查」。
    /// </para>
    /// <para>优先级：finding 原文 &gt; 未建模原因 &gt; 观测依据。</para>
    /// </summary>
    public string Summary => Status switch
    {
        // 逐报文产 finding 的原语（cycle_tolerance 最典型）一条规则可能挂十几条：
        // 全拼出来会在 360px 面板里占满整屏，把其余规则挤出视野。
        // 只显示首条 + 条数，完整列表本来就在事件表与导出报告里。
        NmRuleStatus.Triggered => Messages.Count switch
        {
            0 => "规则判定违规",
            1 => Messages[0],
            _ => string.Create(CultureInfo.InvariantCulture,
                $"{Messages[0]}（另有 {Messages.Count - 1} 条同类问题）"),
        },
        // 「不适用」有两种来源：真的没出现事件，或出现了但都不满足适用条件
        // （如 with_burst 选择器把所有段都跳过了）。后一种情况下 evaluator 仍会
        // 产观测——此时不能断言「未出现」，否则与同一份报告里的观测自相矛盾。
        NmRuleStatus.NotApplicable => AppendCode(
            !string.IsNullOrWhiteSpace(Reason) ? Reason!
            : ObservationCount > 0
                ? string.Create(CultureInfo.InvariantCulture,
                    $"已观测到 {ObservationCount} 条，但均不满足该规则的适用条件")
                : "本次 trace 未出现该规则要判的事件"),
        NmRuleStatus.Unmodeled => AppendCode(
            string.IsNullOrWhiteSpace(Reason) ? "未给出可判定的结论" : Reason!),
        _ => ObservationCount > 0
            ? string.Create(CultureInfo.InvariantCulture,
                $"已检查 {ObservationCount} 条观测，未发现违规")
            : "已检查，未发现违规",
    };

    /// <summary>原因码以括号附在说明尾部；无码时不加空括号。</summary>
    private string AppendCode(string text)
        => string.IsNullOrWhiteSpace(ReasonCode) ? text : $"{text}（{ReasonCode}）";

    /// <summary>
    /// 首个证据时刻（spec §10.2「点击 seek 到证据时刻」）；null = 无可跳转的证据。
    /// <para>
    /// 单独给一个属性而不是让 UI 取 <see cref="EvidenceTimes"/> 的首项：
    /// XAML 绑定拿不到「集合首元素」，而空集合的首项访问会抛异常。
    /// </para>
    /// </summary>
    public double? FirstEvidenceTime => EvidenceTimes.Count > 0 ? EvidenceTimes[0] : null;

    /// <summary>是否可跳转（有证据时刻）。用于 UI 的 IsEnabled / Cursor。</summary>
    public bool CanSeek => FirstEvidenceTime is not null;

    /// <summary>
    /// 排序权重：结论越需要关注，值越小。触发 &gt; 未建模/不适用 &gt; 未触发。
    /// <para>
    /// 「未建模」排在「未触发」之前：用户扫表时先看没查成的，再看查过没问题的，
    /// 扫到「未触发」就可以停。
    /// </para>
    /// </summary>
    internal int SeverityRank => Status switch
    {
        NmRuleStatus.Triggered => 0,
        NmRuleStatus.Unmodeled => 1,
        NmRuleStatus.NotApplicable => 2,
        _ => 3,
    };
}

/// <summary>
/// 规则表里的一组行：结构相同（同原语 + 同期望 + 同 finding 码 + 同条款）的规则。
/// <para>
/// <b>为什么需要分组</b>：吉利 §5.4.3 有 6 条预留位检查、§5.4.4 有 7 条，
/// 每条一个 byte.bit，在 360px 面板里占掉半屏。它们查的是同一类事，
/// 逐条平铺让最该看的触发行被淹掉。折叠后一组只占一行，展开才看逐条。
/// </para>
/// <para>
/// <b>条款必须进分组键</b>：13 条预留位规则原语完全相同，但 6 条查 CBV byte1
/// （§5.4.3）、7 条查 NMSts byte3（§5.4.4）。只按原语分组会把它们并成一组，
/// 标题只能取首行「CBV byte1.bit1」——把 §5.4.4 的 7 条规则描述成了 byte1.bit1。
/// 对一个以条款覆盖为卖点的面板，这等于错误陈述覆盖范围。
/// </para>
/// <para>
/// <b>只在结论一致且低信息量时折叠</b>：组内一条触发、一条未触发，折叠成
/// 「2 条未触发」会把违规藏起来。全「触发」时同样展开——红字标题只说
/// 「有违规」，具体哪条、finding 原文、证据时刻都在展开之后。
/// </para>
/// </summary>
public sealed record NmCalibrationGroup(
    IReadOnlyList<NmCalibrationRow> Rows,
    string Title)
{
    /// <summary>
    /// 是否折叠。三条同时满足才折叠：
    /// <list type="number">
    /// <item>组内多于一行；</item>
    /// <item>结构身份非空；</item>
    /// <item>结论一致，且**不是**「触发」或「未建模」——这两类都需要用户逐条看。</item>
    /// </list>
    /// </summary>
    public bool IsCollapsed => Rows.Count > 1
        && Rows.All(r => !string.IsNullOrEmpty(r.StructuralKey))
        && Rows.Select(r => r.Status).Distinct().Count() == 1
        && Rows[0].Status is NmRuleStatus.NotTriggered or NmRuleStatus.NotApplicable;

    /// <summary>
    /// 组的一句话摘要：结论分布（如「13 条 · 全部未触发」或「3 条 · 1 触发 · 2 未触发」）。
    /// </summary>
    public string Summary => NmCalibrationTable.Summarize(Rows);

    /// <summary>
    /// 折叠行是否含违规——UI 据此决定标题配色。
    /// 用户扫到折叠行就该知道这里有问题，不必展开。
    /// </summary>
    public bool HasViolation => Rows.Any(r => r.Status == NmRuleStatus.Triggered);

    /// <summary>组内含「未建模」行：折叠头要用琥珀色，与单行的「未建模」一致。</summary>
    public bool HasUnmodeled => Rows.Any(r => r.Status == NmRuleStatus.Unmodeled);
}

/// <summary>观测 vs 期望的对比指标（spec §10.2）。</summary>
public enum NmCalibrationMetric
{
    /// <summary>报文周期：观测中位数/P95 vs profile 声明的期望周期。</summary>
    CycleMs = 0,
}

/// <summary>
/// 一条「观测 vs 期望」对比。
/// <para>
/// <see cref="Observed"/> 与 <see cref="Expected"/> 都是**已格式化的文本**：
/// 面板要显示的是「500 vs 520」，而不同指标的有效位数不同（周期是整数毫秒、
/// 容差是百分比），把格式化留在 Core 可以保证报告导出与面板显示一致。
/// </para>
/// </summary>
public sealed record NmCalibrationComparison(
    NmCalibrationMetric Metric,
    string Subject,
    string Expected,
    string Observed,
    bool WithinTolerance);

/// <summary>未建模条款的一行（spec §10.2「显式列出，附条款号」）。</summary>
public sealed record NmUnmodeledClauseRow(
    string? RuleId,
    IReadOnlyList<string> Clauses,
    string Reason,
    string? ReasonCode)
{
    /// <summary>
    /// 条款号的可显示文本（如「§5.4.4 / §5.4.5」）。
    /// <para>
    /// spec §10.2 要求「附条款号」，而 XAML 绑不了集合——给一个拼好的字符串，
    /// 多条款规则（<c>NM_STOP_SENDING → NM_09/NM_12</c>）才不会只显示第一个。
    /// </para>
    /// </summary>
    public string ClausesText => Clauses.Count == 0 ? "" : string.Join(" / ", Clauses);
}

/// <summary>
/// 标定面板的数据构建（spec §10.2）。
/// <para>
/// <b>为什么放在 Core 而不是 VM</b>：三态映射、排序、观测 vs 期望的取值口径
/// 都是「报告怎么读」的语义，与 WPF 无关。放 Core 才能在不启动 UI 的情况下测。
/// </para>
/// <para>
/// <b>结论映射按 RuleId 关联、按引擎 verdict 定性，不看文案</b>：finding/observation/
/// unmodeled 的文案会随措辞调整变化，用它判断会让「触发」在改一句话后静默变成
/// 「未触发」。有 finding 一律算触发（安全优先）；无 finding 时以
/// <see cref="NmReport.RuleEvaluations"/> 为准，verdict 缺失才回退反推。
/// </para>
/// </summary>
public static partial class NmCalibrationTable
{
    /// <summary>
    /// 构建规则表。<paramref name="rules"/> 来自展平 profile，顺序不参与输出——
    /// 输出按结论强度排序（触发 → 未建模 → 不适用 → 未触发），同强度内按
    /// RuleId ordinal，保证同一份报告每次刷新的行序一致。
    /// </summary>
    public static IReadOnlyList<NmCalibrationRow> Build(
        IReadOnlyList<NmRuleDef> rules, NmReport report)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(report);

        var findingsByRule = (report.Findings ?? Array.Empty<NmFinding>())
            .Where(f => f.RuleId is not null)
            .ToLookup(f => f.RuleId!, StringComparer.Ordinal);
        var observationsByRule = (report.Observations ?? Array.Empty<NmObservation>())
            .Where(o => o.RuleId is not null)
            .ToLookup(o => o.RuleId!, StringComparer.Ordinal);
        var unmodeledByRule = (report.UnmodeledRules ?? Array.Empty<NmUnmodeledRule>())
            .Where(u => u.RuleId is not null)
            .ToLookup(u => u.RuleId!, StringComparer.Ordinal);
        // 引擎的逐规则结论。空 = 没跑规则引擎（未选中企标），此时只能反推。
        var verdictByRule = (report.RuleEvaluations ?? Array.Empty<NmRuleEvaluation>())
            .ToLookup(e => e.RuleId, StringComparer.Ordinal);

        var rows = new List<NmCalibrationRow>(rules.Count);
        foreach (var rule in rules)
        {
            var findings = findingsByRule[rule.Id].ToArray();
            var observations = observationsByRule[rule.Id].ToArray();
            var unmodeled = unmodeledByRule[rule.Id].FirstOrDefault();
            var evaluation = verdictByRule[rule.Id].FirstOrDefault();

            var status = ResolveStatus(report, findings, observations, unmodeled, evaluation);

            rows.Add(new NmCalibrationRow(
                rule.Id,
                rule.Requirement,
                status,
                rule.Clauses ?? Array.Empty<string>(),
                findings.Select(f => f.Message).ToArray(),
                findings.SelectMany(f => f.EvidenceTimes).ToArray(),
                ResolveReasonCode(findings, unmodeled, evaluation),
                unmodeled?.Reason)
            {
                Verdict = evaluation?.Status,
                ObservationCount = observations.Length,
                // 结构身份：同原语 + 同期望 + 同 finding 码 + 同条款 = 查的是同一类事。
                // 只有 byte/bit 这类参数不同，正是适合折叠的情形。
                //
                // 条款必须进键：13 条预留位规则原语完全相同，但 6 条查 CBV byte1
                // （§5.4.3）、7 条查 NMSts byte3（§5.4.4）。不按条款拆，一组会横跨
                // 两个条款，折叠标题只能描述其中之一——等于错误陈述覆盖范围。
                StructuralKey = $"{rule.When.Kind}|{rule.Expect.Kind}|{rule.FindingCode}|"
                    + string.Join(",", (rule.Clauses ?? Array.Empty<string>())
                        .Order(StringComparer.Ordinal)),
            });
        }

        return rows
            .OrderBy(r => r.SeverityRank)
            .ThenBy(r => r.RuleId, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// 结论映射。
    /// <para>
    /// <b>有 finding 一律算触发</b>，不看 verdict：展示层宁可多报一条违规，
    /// 也不能因为 verdict 异常把真实违规藏起来。
    /// </para>
    /// <para>
    /// 没有 finding 时**以引擎 verdict 为准**——原实现自己反推，把
    /// <see cref="NmRuleVerdict.NotApplicable"/> 显示成「未触发」，让用户以为查过了。
    /// verdict 缺失（旧报告 / 未选中企标）才回退到反推。
    /// </para>
    /// <para>
    /// <b>回退路径的边界</b>：状态映射逐分支与改动前等价；但行序由
    /// <c>Build</c> 统一按 <see cref="NmCalibrationRow.SeverityRank"/> 排，
    /// 这条对旧报告同样生效——行序变了，状态没变。
    /// </para>
    /// </summary>
    private static NmRuleStatus ResolveStatus(
        NmReport report,
        NmFinding[] findings, NmObservation[] observations,
        NmUnmodeledRule? unmodeled, NmRuleEvaluation? evaluation)
    {
        if (findings.Length > 0) return NmRuleStatus.Triggered;

        if (evaluation is not null)
        {
            // 显式列全，与 NmReportWriter 同口径：新增枚举值时编译期暴露，
            // 不静默归一到「未建模」。
            return evaluation.Status switch
            {
                NmRuleVerdict.EvaluatedNoFinding => NmRuleStatus.NotTriggered,
                NmRuleVerdict.NotApplicable => NmRuleStatus.NotApplicable,
                NmRuleVerdict.Unmodeled => NmRuleStatus.Unmodeled,
                // 部分判成：已确证的部分照常产出结论，另一部分判不了。
                // 呈现上归「未建模」（不能声称整条合规），但保留 verdict 供 UI 区分。
                NmRuleVerdict.Partial => NmRuleStatus.Unmodeled,
                // 引擎声称有违规却没给 finding：内部不一致，不能当合规。
                // 上面 findings.Length > 0 已经拦掉正常情况，走到这里说明引擎自相矛盾。
                NmRuleVerdict.EvaluatedWithFinding => NmRuleStatus.Unmodeled,
                _ => throw new InvalidOperationException(
                    $"未映射的规则求值状态：{evaluation.Status}"),
            };
        }

        // —— 以下为无 verdict 时的回退路径（保持旧行为）——
        //
        // 优先序：Unmodeled > NotTriggered。部分原语会「一部分报文判成了、
        // 另一部分因共享 ID / 数据不足判不了」（nmsts_transition 最典型）。
        // 这种规则同时有 observation 和 unmodeled 条目；若让 observation 优先，
        // 行会显示「未触发」却带着 Unmodeled 原因，并与「未建模条款」区
        // 重复出现——状态列与列表自相矛盾。宁可显示「未建模」，
        // 因为「有部分没判成」比「没违规」更接近事实。
        if (unmodeled is not null) return NmRuleStatus.Unmodeled;
        if (observations.Length > 0) return NmRuleStatus.NotTriggered;

        // 完整报告里既无 finding 也无 observation 也无 unmodeled：
        // 说明求值器返回了 true 且未给原因 = **跑过了、没违规**。
        //
        // 这覆盖 layout_check / cycle_tolerance / burst_shape 这类
        // 「只在违规时产出、通过时静默」的原语。若把它们判成 Unmodeled，
        // 用户会以为标定面板漏了一条本来检查过的规则——把「查过没问题」
        // 报成「没查」是标定面板最不能出的错。
        //
        // partial 报告则相反：引擎没跑完全部规则，这条可能压根没被求值，
        // 既不能说合规也不能说违规，只能报「未建模」。
        return report.IsPartial ? NmRuleStatus.Unmodeled : NmRuleStatus.NotTriggered;
    }

    /// <summary>
    /// 行的原因码。
    /// <para>
    /// 优先取引擎的：<see cref="NmReport.UnmodeledRules"/> 里的条目只覆盖「未建模」，
    /// 而 <see cref="NmRuleVerdict.Partial"/> 的码只挂在 evaluation 上。
    /// </para>
    /// <para>
    /// 特殊情况：引擎声称有违规（<see cref="NmRuleVerdict.EvaluatedWithFinding"/>）
    /// 却没有任何 finding——这是**代码缺陷**，不是 profile 或数据问题。必须显式标
    /// <see cref="NmUnmodeledCodes.InternalInconsistent"/>，不能让它伪装成普通未建模，
    /// 否则 AI 会指引用户去改一份本来没问题的 profile。
    /// </para>
    /// </summary>
    private static string? ResolveReasonCode(
        NmFinding[] findings, NmUnmodeledRule? unmodeled, NmRuleEvaluation? evaluation)
    {
        if (findings.Length == 0
            && evaluation?.Status == NmRuleVerdict.EvaluatedWithFinding)
        {
            return NmUnmodeledCodes.InternalInconsistent;
        }

        return unmodeled?.ReasonCode ?? evaluation?.ReasonCode;
    }

    /// <summary>
    /// 把已排好序的行按结构身份分组。
    /// <para>
    /// 输入顺序决定输出顺序（行已按结论强度排好）：组按**首行**的位置出现，
    /// 组内保持原顺序。这样「触发」永远在最前面，不会被折叠挪到后面。
    /// </para>
    /// <para>
    /// 结构身份为空的行（手工构造）各自成组，永不折叠。
    /// </para>
    /// </summary>
    public static IReadOnlyList<NmCalibrationGroup> Group(IReadOnlyList<NmCalibrationRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var groups = new List<NmCalibrationGroup>();
        // 「身份 → 已建组下标」的原地累加：组要按首行出现的位置排（结论强度序），
        // 组内保持原序。用 Dictionary 记下标是为了保留这个位置语义。
        var index = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (string.IsNullOrEmpty(row.StructuralKey))
            {
                groups.Add(new NmCalibrationGroup([row], row.Title));
                continue;
            }

            if (index.TryGetValue(row.StructuralKey, out var at))
            {
                IReadOnlyList<NmCalibrationRow> merged = [.. groups[at].Rows, row];
                groups[at] = groups[at] with { Rows = merged, Title = GroupTitle(merged) };
            }
            else
            {
                index[row.StructuralKey] = groups.Count;
                groups.Add(new NmCalibrationGroup([row], GroupTitle([row])));
            }
        }

        return groups;
    }

    /// <summary>
    /// 组标题：从**结构**派生，不复用某一行 Requirement。
    /// <para>
    /// <b>为什么不能取首行</b>：13 条预留位规则的 Requirement 是
    /// 「CBV byte1.bit1 为预留位」这种带具体 byte.bit 的句子。取首行会让
    /// 整组（含 §5.4.4 的 7 条 byte3 规则）被描述成 byte1.bit1——
    /// 对一个以条款覆盖为卖点的面板，这是错误陈述覆盖范围。
    /// </para>
    /// <para>
    /// 单行组直接用该行的 Title（那时它是准确的）；多行组去掉参数，
    /// 只留「查什么」这一类语义，参数差异由展开后的逐条行提供。
    /// </para>
    /// </summary>
    private static string GroupTitle(IReadOnlyList<NmCalibrationRow> rows)
    {
        if (rows.Count == 1) return rows[0].Title;

        // 去掉开头的「<前缀> byteN.bitM 」参数片段，只留共同语义。
        // 例：「CBV byte1.bit1 为预留位，必须填 0」→「为预留位，必须填 0」。
        // 只剥这一小段而不是按逗号切：逗号后面也可能有语义，按逗号切会把
        // 「预留位」这种关键信息一起丢掉。
        var first = rows[0].Title;
        var semantic = ParamPrefix().Replace(first, "").Trim();
        if (string.IsNullOrWhiteSpace(semantic)) semantic = first;

        // 条款去重后列出：跨条款的组必须让用户看到它覆盖了哪些条款。
        var clauses = rows
            .SelectMany(r => r.Clauses)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        return clauses.Length > 0
            ? $"{semantic}（{string.Join(" / ", clauses)}）"
            : semantic;
    }

    /// <summary>
    /// 匹配 requirement 开头的「<可选前缀> byteN.bitM 」参数片段。
    /// <para>
    /// 只用于多行组的标题去参数化：单行组的 Title 是准确的，不需要处理。
    /// 匹配不到时原样保留，不会误删。
    /// </para>
    /// </summary>
    [GeneratedRegex(@"^\s*\S*\s*byte\d+\.bit\d+\s+", RegexOptions.CultureInvariant)]
    private static partial Regex ParamPrefix();

    /// <summary>
    /// 面板顶部的一句话汇总（如「4 条 · 1 触发 · 1 未建模 · 2 未触发」）。
    /// <para>
    /// 21 行平铺时用户得自己数哪几条挂了；汇总让「有没有问题」一眼可见。
    /// 为空的类别不出现，避免「0 未建模」这种噪声。
    /// </para>
    /// </summary>
    public static string Summarize(IReadOnlyList<NmCalibrationRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0) return "";

        var parts = new List<string>(4);
        void Add(NmRuleStatus status, string label)
        {
            var count = rows.Count(r => r.Status == status);
            if (count > 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{count} {label}"));
        }

        Add(NmRuleStatus.Triggered, "触发");
        Add(NmRuleStatus.Unmodeled, "未建模");
        Add(NmRuleStatus.NotApplicable, "不适用");
        Add(NmRuleStatus.NotTriggered, "未触发");

        return string.Create(CultureInfo.InvariantCulture,
            $"{rows.Count} 条 · {string.Join(" · ", parts)}");
    }

    /// <summary>
    /// 构建「观测 vs 期望」对比（spec §10.2）。
    /// <para>
    /// 只输出**有期望值**且**样本充分**的指标：没有声明周期的报文拿中位数自比自，
    /// 或有效间隔 &lt; 5 时报一个伪精确的「520ms」，都会让面板比不显示更误导。
    /// </para>
    /// </summary>
    public static IReadOnlyList<NmCalibrationComparison> BuildComparisons(NmReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var comparisons = new List<NmCalibrationComparison>();
        foreach (var message in report.Messages ?? Array.Empty<NmMessageReport>())
        {
            var cycle = message.Cycle;
            if (cycle.InsufficientSamples) continue;
            if (cycle.ExpectedMs is not { } expected || cycle.MedianMs is not { } median) continue;

            var tolerance = cycle.ToleranceMs ?? 0;
            var within = Math.Abs(median - expected) <= tolerance;

            comparisons.Add(new NmCalibrationComparison(
                NmCalibrationMetric.CycleMs,
                message.CanIdHex,
                // 期望值用 invariant 拼：报告会导出，不能随机器区域设置变化。
                tolerance > 0
                    ? string.Create(CultureInfo.InvariantCulture, $"{expected}ms ±{tolerance}ms")
                    : string.Create(CultureInfo.InvariantCulture, $"{expected}ms"),
                string.Create(CultureInfo.InvariantCulture,
                    $"中位数 {median:F0}ms，P95 {cycle.P95Ms:F0}ms，" +
                    $"范围 {cycle.MinMs:F0}–{cycle.MaxMs:F0}ms"),
                within));
        }

        return comparisons;
    }

    /// <summary>数据体检三态表（spec §9.2 / §10.2）。</summary>
    public static IReadOnlyList<NmDataQualityCheck> BuildDataQuality(NmReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return report.DataQualityChecks ?? Array.Empty<NmDataQualityCheck>();
    }

    /// <summary>
    /// 未建模条款（spec §10.2「显式列出，附条款号」）。
    /// <para>
    /// 用 <see cref="NmUnmodeledRule.Clauses"/> 而不是单值 <c>Clause</c>：
    /// 多条款规则（<c>NM_STOP_SENDING → NM_09/NM_12</c>）只读单值会丢条款。
    /// </para>
    /// </summary>
    public static IReadOnlyList<NmUnmodeledClauseRow> BuildUnmodeledClauses(NmReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return (report.UnmodeledRules ?? Array.Empty<NmUnmodeledRule>())
            .Select(u => new NmUnmodeledClauseRow(
                u.RuleId,
                u.Clauses.Count > 0
                    ? u.Clauses
                    : string.IsNullOrEmpty(u.Clause) ? Array.Empty<string>() : [u.Clause],
                u.Reason,
                u.ReasonCode))
            .ToArray();
    }
}
