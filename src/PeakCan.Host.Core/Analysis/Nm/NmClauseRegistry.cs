namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// 条款白名单（spec §4 / §19.3「追溯矩阵最低版」）。
/// <para>
/// <b>要挡的是什么</b>：profile 的 <c>clauses</c> 写了规范里不存在的条款号。
/// 静默接受会让 finding 带一个查不到的条款，追溯链断在源头。
/// </para>
/// <para>
/// <b>挡不住什么（必须明确）</b>：只查「条款号存在」，**不查「规则与条款语义是否匹配」**。
/// 仓库里的活反例：<c>NM_STOP_SENDING</c> 曾写 <c>source_ref: §5.9</c>，而 §5.9 是
/// 「诊断报文不唤醒」（U 级、M3），NM_09 是「RSS 停发」——§5.9 确实存在，
/// 白名单放行。语义错挂只能靠人工 review 或解析 spec §4 表做双向对照。
/// </para>
/// <para>
/// 白名单内容来自 spec §4 的条款覆盖表，**必须与它手工同步**。同步失效的后果是
/// 新条款被误判为「不存在」——比静默接受好，至少会报错而不是悄悄生效。
/// </para>
/// <para>
/// <b>当前只适用于 geely-cv</b>：白名单是吉利 §4 表的硬编码副本，而
/// <see cref="ValidateFlattened"/> 对任何 profile 都套用。spec §1.3 说 schema 预留多厂，
/// 加入第二个 OEM profile 之前**必须**改成按 <c>StandardId</c> 分表，
/// 否则另一家的条款号会被当成「不存在」直接拒绝。
/// </para>
/// </summary>
public static class NmClauseRegistry
{
    /// <summary>
    /// spec §4 表里出现过的全部条款标识。
    /// <list type="bullet">
    /// <item>NM_01–NM_17：吉利规范的行为条款。</item>
    /// <item>§5.4.x / §5.5 / §5.8 / §5.9 / 附录 A：规范章节。</item>
    /// </list>
    /// </summary>
    public static readonly IReadOnlySet<string> KnownClauses =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "NM_01", "NM_02", "NM_03", "NM_04", "NM_05", "NM_06",
            "NM_07", "NM_08", "NM_09", "NM_10", "NM_11", "NM_12",
            "NM_13", "NM_14", "NM_15", "NM_16", "NM_17",
            "§5.4.1", "§5.4.2", "§5.4.3", "§5.4.4", "§5.5",
            "§5.8", "§5.9",
            // 两种写法都收：加载期形状校验的正则是 `附录 ?A`（空格可选），
            // 而报错文案写的是「附录A」（无空格）。只收一种会让照着报错文案
            // 写出来的值被白名单拒绝——那是把人往坑里带。
            "附录 A", "附录A",
        };

    /// <summary>
    /// 校验一组条款标识。全部合法返回 null，否则返回第一条错误说明。
    /// <para>
    /// 空串算错误：它不是「不参与统计」（那用空列表表达），是写错了。
    /// </para>
    /// </summary>
    public static string? Validate(IReadOnlyList<string> clauses)
    {
        ArgumentNullException.ThrowIfNull(clauses);

        foreach (var clause in clauses)
        {
            if (string.IsNullOrWhiteSpace(clause))
            {
                return "clauses 含空项；不参与条款统计请用空列表，不要留空串";
            }
            if (!KnownClauses.Contains(clause))
            {
                return $"条款「{clause}」不在 spec §4 的条款表中（合法值见 NmClauseRegistry.KnownClauses）";
            }
        }
        return null;
    }

    /// <summary>
    /// 加载期校验：展平后的每条规则的 <c>clauses</c> 都必须在白名单内。
    /// <para>
    /// 空 <c>clauses</c> 合法——base 规则不属于 OEM 条款空间（spec §5.5
    /// 「按归因而非按 profile 类型判定」）。
    /// </para>
    /// </summary>
    public static void ValidateFlattened(NmStandardFlattener.Flattened flat)
    {
        ArgumentNullException.ThrowIfNull(flat);

        foreach (var rule in flat.Rules ?? Array.Empty<NmRuleDef>())
        {
            if (rule.Clauses.Count == 0) continue;

            if (Validate(rule.Clauses) is { } error)
            {
                throw new NmProfileValidationException($"规则 {rule.Id} 的 {error}");
            }
        }
    }
}
