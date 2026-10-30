using System;
using System.Collections.Generic;
using System.Linq;

namespace PeakCan.Host.App.ViewModels.Xcp;

/// <summary>
/// 把 XCP 状态区的检查明细压成「按类摘要 + 少量样例」。
/// <para>
/// 存在的理由：<c>Asap2PackageApi.CollectCrossChecks</c> 对「地址缺失」按<b>每个对象</b>
/// 报一条，真机 A2L（S32K148_EB_IarPrj_CANape.a2l，3000 个对象全地址 0x0）实测
/// 产出 3003 条。逐条上屏既撑爆布局（连接区所在 Grid 行是 Auto）也没有阅读价值。
/// </para>
/// <para>
/// <b>不许静默降级</b>：被折叠的条数必须显式写出来，完整清单仍留在调用方的原始
/// 集合（<c>ValidationNotes</c> / <c>CapabilityReport</c>）里——本类只决定「怎么摆」，
/// 不丢数据。单条分组输出与折叠前逐字一致，正常 A2L 的状态区不会凭空多出摘要行。
/// </para>
/// </summary>
public static class XcpStatusLineCollapser
{
    /// <summary>每类最多平铺几条样例（够看出是什么问题即可）。</summary>
    public const int MaxDetailsPerGroup = 3;

    /// <summary>
    /// 组内条目不超过这个数就全部平铺。
    /// <para>
    /// 为什么要分档：有些组条目不多但<b>每一条都可操作</b>——「缺哪几条 REQUIRED_CMD」
    /// 最多十几条、地址待回填的对象名就是回填清单本身。这类组截断后界面上没有任何
    /// 取回路径（<c>ValidationNotes</c> / <c>CapabilityReport</c> 只有代码能读），
    /// 等于把工作量藏起来了。真正需要折叠的是洪水组（地址缺失 3000 条）。
    /// </para>
    /// </summary>
    public const int AlwaysShowAllUpTo = 20;

    private const string DetailIndent = "    ";

    /// <summary>
    /// 按 <paramref name="items"/> 的 Key 分组。多条的组输出「一行摘要 + 明细」：
    /// 明细 ≤ <see cref="AlwaysShowAllUpTo"/> 条时全列，否则只留
    /// <see cref="MaxDetailsPerGroup"/> 条样例再补一行省略说明；
    /// <b>硬约束级（Error / Fatal / Reject）的组一律全列</b>，不截断。
    /// 组的顺序 = 首次出现顺序（状态区顺序必须稳定，不能随字典序跳）。
    /// </summary>
    /// <param name="items">(分组键, 严重度, 明细文本)。明细文本不要自带严重度前缀。</param>
    /// <param name="keyLabel">摘要里用的类别名；缺省直接用 Key 的 ToString。</param>
    public static IReadOnlyList<string> Collapse<TKey>(
        IEnumerable<(TKey Key, string Severity, string Text)> items,
        Func<TKey, string>? keyLabel = null)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(items);

        var order = new List<TKey>();
        var groups = new Dictionary<TKey, List<(string Severity, string Text)>>();
        foreach (var (key, severity, text) in items)
        {
            if (!groups.TryGetValue(key, out var bucket))
            {
                groups[key] = bucket = new List<(string Severity, string Text)>();
                order.Add(key);
            }
            bucket.Add((severity, text));
        }

        var lines = new List<string>(order.Count);
        foreach (var key in order)
        {
            var bucket = groups[key];
            if (bucket.Count == 1)
            {
                // 单条不进摘要形态：绝大多数类别只有一条，多一行「× 1」纯属噪音。
                lines.Add($"[{bucket[0].Severity}] {bucket[0].Text}");
                continue;
            }

            // 取组内最严重的 severity：同一分组键理论上可以混级（今天不会），
            // 按首条报会在那种情况下少报，摘要头写错了比数字错了更难发现。
            var severity = bucket.OrderByDescending(x => SeverityRank(x.Severity)).First().Severity;
            var label = keyLabel?.Invoke(key) ?? key.ToString();
            lines.Add($"[{severity}] {label} × {bucket.Count}");

            var shown = IsHardConstraint(severity) || bucket.Count <= AlwaysShowAllUpTo
                ? bucket.Count
                : Math.Min(bucket.Count, MaxDetailsPerGroup);
            for (var i = 0; i < shown; i++)
                lines.Add(DetailIndent + bucket[i].Text);

            if (bucket.Count > shown)
            {
                lines.Add(DetailIndent +
                    $"… 另有 {bucket.Count - shown} 条同类明细已省略（完整清单保留在原始结果中，未丢弃）");
            }
        }

        return lines;
    }

    /// <summary>
    /// 严重度排序（数值越大越严重）。两套来源的枚举名都在这里：
    /// <c>ErrorSeverity</c> = Warning/Error/Fatal，<c>XcpCapabilitySeverity</c> = Warning/Reject。
    /// 未知值按 Warning 对待。
    /// </summary>
    private static int SeverityRank(string severity) => severity switch
    {
        "Reject" => 3,
        "Fatal" => 2,
        "Error" => 1,
        _ => 0,
    };

    /// <summary>
    /// 硬约束级不截断：这类组的每条通常指名一个<b>不同的</b>对象或命令码
    /// （例如 <c>COMMAND_DECLARED_NOT_MEASURED</c> 一条一个命令码），
    /// 截掉就等于让人看不到要修什么。这类组的条数天然有界（≤ 声明的命令码数），
    /// 按对象发射的洪水组（地址缺失 3000 条）永远是 Warning 级，不会混进来。
    /// 万一将来出现超大拒绝组，布局的硬保护是 XcpView 状态区的限高滚动区。
    /// </summary>
    private static bool IsHardConstraint(string severity) => SeverityRank(severity) >= 1;
}
