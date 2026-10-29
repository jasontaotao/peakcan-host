using CommunityToolkit.Mvvm.Input;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// P0-2：Trace 行右键菜单命令——只看 / 排除 / 高亮当前帧的 ID。
/// "只看"复用 ID allow-list 管线；"排除"走独立 <see cref="_excludedIds"/>
/// 绝对排除维度（不反转 ExcludeMatch 的整体取反）；"高亮"追加规则集。
/// </summary>
public sealed partial class TraceViewModel
{
    /// <summary>
    /// 帧 ID 的过滤文本形式。复用 <see cref="CanId.ToString"/>——界面上 ID 列
    /// 显示宽度（标准 3 位 / 扩展 8 位 hex）与写入过滤框的文本保持一致，
    /// 用户对着界面找字符串不会对不上。
    /// </summary>
    internal static string FormatIdHex(TraceEntry entry) => entry.Id.ToString();

    /// <summary>
    /// 只看这个 ID：覆盖 ID allow-list，清掉 DBC 消息名（其解析结果会并集
    /// 扩进 allow-list，与"只看"矛盾），并关闭"排除"整体取反。
    /// 通道/仅错误帧等 AND 条件保留——它们只会进一步收窄结果。
    /// </summary>
    [RelayCommand]
    private void ShowOnlyThisId(TraceEntry? entry)
    {
        if (entry is null) return;
        DbcMessageName = "";
        IdListText = FormatIdHex(entry);
        ExcludeMatch = false;
    }

    /// <summary>
    /// 排除这个 ID：加入绝对排除集合后重建 spec。不碰 IdListText/ExcludeMatch，
    /// 因此"仅错误帧 / PGN / 通道 / payload"等其他条件保持原语义。
    /// </summary>
    [RelayCommand]
    private void ExcludeThisId(TraceEntry? entry)
    {
        if (entry is null) return;
        if (_excludedIds.Add(entry.Id.Raw))
        {
            _excludedIdLabels[entry.Id.Raw] = FormatIdHex(entry);
            ApplyExclusionChange();
        }
    }

    /// <summary>清除行右键产生的全部 ID 排除（不影响其他过滤条件）。</summary>
    [RelayCommand]
    private void ClearExcludedIds()
    {
        if (_excludedIds.Count == 0) return;
        _excludedIds.Clear();
        _excludedIdLabels.Clear();
        ApplyExclusionChange();
    }

    /// <summary>
    /// 高亮这个 ID：追加一条只含该 ID 的高亮规则。同一 ID 已存在规则时跳过
    /// （避免重复行）；插入位置放在第一条"匹配全部"兜底规则之前，避免被
    /// 兜底规则先匹配先赢而永远不生效。
    /// </summary>
    [RelayCommand]
    private void HighlightThisId(TraceEntry? entry)
    {
        if (entry is null) return;
        var idText = FormatIdHex(entry);
        // 仅对"已启用"的同 ID 规则去重：禁用规则不生效，用户重新高亮时应新建。
        if (HighlightRules.Any(r => r.Enabled &&
                string.Equals(r.IdListText, idText, StringComparison.OrdinalIgnoreCase)))
            return;

        var insertAt = HighlightRules.Count;
        for (int i = 0; i < HighlightRules.Count; i++)
        {
            if (HighlightRules[i].Enabled && HighlightRules[i].IsMatchAll)
            {
                insertAt = i;
                break;
            }
        }
        AddHighlightRuleCore(idText, HighlightRules.Count % 6, insertAt);
    }
}
