using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// 2026-08-31 P3：多规则彩色高亮（spec §5.6）。<see cref="HighlightRules"/> 行集，
/// 每行组装一个仅含 IdAllowList/PgnList 的 <see cref="TraceFilterSpec"/> 复用
/// <see cref="TraceFilterSpec.Matches"/>——零重复谓词代码。求值自上而下先匹配先赢。
/// </summary>
public sealed partial class TraceViewModel
{
    /// <summary>高亮规则行集。</summary>
    public ObservableCollection<HighlightRuleRowViewModel> HighlightRules { get; } = new();

    /// <summary>收起时摘要文本："N 条规则生效"（启用中的规则数）。</summary>
    [ObservableProperty]
    private string _highlightSummaryText = "0 条规则生效";

    /// <summary>新加一条规则行。</summary>
    [RelayCommand]
    private void AddHighlightRule()
    {
        var row = new HighlightRuleRowViewModel();
        // 用命名 handler 订阅，RemoveHighlightRule 才能正确退订（lambda 退订不匹配）。
        row.PropertyChanged += OnHighlightRuleRowChanged;
        HighlightRules.Add(row);
        RecomputeAllHighlights();
    }

    /// <summary>移除指定规则行。</summary>
    [RelayCommand]
    private void RemoveHighlightRule(HighlightRuleRowViewModel row)
    {
        if (HighlightRules.Remove(row))
        {
            row.PropertyChanged -= OnHighlightRuleRowChanged;
            RecomputeAllHighlights();
        }
    }

    /// <summary>规则行属性变更 → 全量重算高亮（行内非法时 ErrorText 已置，求值跳过该行）。</summary>
    private void OnHighlightRuleRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        => RecomputeAllHighlights();

    /// <summary>
    /// 对新行求高亮色索引（0..5，-1=无）。规则自上而下，先匹配先赢；行内文本非法
    /// 或行禁用 → 跳过。谓词：每行现场组装 TraceFilterSpec（仅 IdAllowList/PgnList）复用 Matches。
    /// internal 供 TraceHighlightRuleTests MTA 直驱。
    /// </summary>
    internal int EvaluateHighlight(TraceEntry entry)
    {
        foreach (var rule in HighlightRules)
        {
            if (!rule.Enabled) continue;
            var (spec, error) = TraceFilterParser.TryParse(
                rule.IdListText, rule.PgnListText, null, null, null, null,
                null, null, null);
            if (error is not null)
            {
                // 行内非法 → 该行不匹配（ErrorText 由行 VM setter 维护）。
                continue;
            }
            if (rule.IsMatchAll || spec!.Matches(entry))
                return rule.ColorIndex;
        }
        return -1;
    }

    /// <summary>规则集/行属性变更 → 遍历 Entries 全量重算高亮色。internal 供测试直驱。</summary>
    internal void RecomputeAllHighlights()
    {
        int activeCount = 0;
        foreach (var rule in HighlightRules)
            if (rule.Enabled) activeCount++;
        HighlightSummaryText = $"{activeCount} 条规则生效";

        foreach (var entry in Entries)
            entry.HighlightColorIndex = EvaluateHighlight(entry);
    }
}
