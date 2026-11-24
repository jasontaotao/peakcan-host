using System.Globalization;

namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>规则在本次分析里的三态结论（spec §10.2 标定面板）。</summary>
public enum NmRuleStatus
{
    /// <summary>有该 RuleId 的 finding：规则判定违规。</summary>
    Triggered = 0,

    /// <summary>有该 RuleId 的观测、无 finding：规则跑了、检查了、没发现违规。</summary>
    NotTriggered = 1,

    /// <summary>规则没给出可判定的结论（原因码见 <see cref="NmCalibrationRow.ReasonCode"/>）。</summary>
    Unmodeled = 2,
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
        _ => "未建模",
    };

    /// <summary>该规则产出的观测条数（用于「未触发」时说清「基于几条观测」）。</summary>
    public int ObservationCount { get; init; }

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
/// <b>三态映射只看 RuleId</b>：finding/observation/unmodeled 的文案会随措辞调整
/// 变化，用它判断会让「触发」在改一句话后静默变成「未触发」。
/// </para>
/// </summary>
public static class NmCalibrationTable
{
    /// <summary>
    /// 构建规则表。<paramref name="rules"/> 来自展平 profile，顺序不参与输出——
    /// 输出按 RuleId ordinal 排序，保证同一份报告每次刷新的行序一致。
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

        var rows = new List<NmCalibrationRow>(rules.Count);
        foreach (var rule in rules)
        {
            var findings = findingsByRule[rule.Id].ToArray();
            var observations = observationsByRule[rule.Id].ToArray();
            var unmodeled = unmodeledByRule[rule.Id].FirstOrDefault();

            // 优先序：Triggered > Unmodeled > NotTriggered。
            //
            // Unmodeled 排在 NotTriggered 之前：部分原语会「一部分报文判成了、
            // 另一部分因共享 ID / 数据不足判不了」（nmsts_transition 最典型）。
            // 这种规则同时有 observation 和 unmodeled 条目；若让 observation 优先，
            // 行会显示「未触发」却带着 Unmodeled 原因，并与「未建模条款」区
            // 重复出现——状态列与列表自相矛盾。宁可显示「未建模」，
            // 因为「有部分没判成」比「没违规」更接近事实。
            NmRuleStatus status;
            if (findings.Length > 0)
            {
                status = NmRuleStatus.Triggered;
            }
            else if (unmodeled is not null)
            {
                // 引擎明确报「这条没判成」，原因码见行上。
                status = NmRuleStatus.Unmodeled;
            }
            else if (observations.Length > 0)
            {
                // 有观测无 finding：规则确实跑了并检查过，只是没发现违规。
                status = NmRuleStatus.NotTriggered;
            }
            else if (!report.IsPartial)
            {
                // 完整报告里既无 finding 也无 observation 也无 unmodeled：
                // 说明求值器返回了 true 且未给原因 = **跑过了、没违规**。
                //
                // 这覆盖 layout_check / cycle_tolerance / burst_shape 这类
                // 「只在违规时产出、通过时静默」的原语。若把它们判成 Unmodeled，
                // 用户会以为标定面板漏了一条本来检查过的规则——把「查过没问题」
                // 报成「没查」是标定面板最不能出的错。
                status = NmRuleStatus.NotTriggered;
            }
            else
            {
                // partial 报告：引擎没跑完全部规则，这条可能压根没被求值。
                // 此时既不能说合规也不能说违规，只能报「未建模」。
                status = NmRuleStatus.Unmodeled;
            }

            rows.Add(new NmCalibrationRow(
                rule.Id,
                rule.Requirement,
                status,
                rule.Clauses ?? Array.Empty<string>(),
                findings.Select(f => f.Message).ToArray(),
                findings.SelectMany(f => f.EvidenceTimes).ToArray(),
                unmodeled?.ReasonCode,
                unmodeled?.Reason)
            {
                ObservationCount = observations.Length,
            });
        }

        return rows.OrderBy(r => r.RuleId, StringComparer.Ordinal).ToArray();
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
