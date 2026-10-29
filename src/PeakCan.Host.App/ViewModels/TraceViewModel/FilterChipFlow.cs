using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

namespace PeakCan.Host.App.ViewModels;

/// <summary>过滤 chip 的类别标识，供 XAML 着色（字符串比枚举更易在 DataTrigger 里写）。</summary>
public static class FilterChipKinds
{
    public const string Id = "id";
    public const string Pgn = "pgn";
    public const string Sa = "sa";
    public const string Da = "da";
    public const string Dbc = "dbc";
    public const string Channel = "channel";
    public const string ErrorsOnly = "errors";
    public const string ExcludeMode = "exclude";
    public const string ExcludedId = "excluded-id";
    public const string Payload = "payload";
}

/// <summary>
/// 一条已生效的过滤条件 chip。移除命令直接改写对应的 VM 字段，
/// 走既有解析管线，不绕过 <see cref="TraceFilterSpec"/>。
/// </summary>
public sealed class FilterChipViewModel
{
    public string Label { get; }
    public string Kind { get; }
    public ICommand RemoveCommand { get; }

    public FilterChipViewModel(string label, string kind, Action remove)
    {
        Label = label;
        Kind = kind;
        RemoveCommand = new RelayCommand(remove);
    }
}

/// <summary>
/// P0-3：过滤条件 chip 条。把散落在 6 个文本框/3 个复选框里的条件投影为
/// 可单条删除的标签——用户随时看清"当前叠了哪些条件"。
/// 数据源是 VM 字段本身（不读 <see cref="_activeSpec"/>），因此非法文本
/// 也能以 chip 形式呈现并就地删除。
/// </summary>
public sealed partial class TraceViewModel
{
    /// <summary>当前生效的过滤条件 chip（顺序：ID → PGN → SA → DA → 其他）。</summary>
    public ObservableCollection<FilterChipViewModel> FilterChips { get; } = new();

    /// <summary>与 <c>CanIdListParser</c> / parser 保持一致的 token 分隔符。</summary>
    private static readonly char[] ChipTokenSeparators = { ',', ' ', '\t', '\n', '\r' };

    /// <summary>按当前字段重建 chip 列表（字段变更 → 解析管线 → 此处）。</summary>
    private void RebuildFilterChips()
    {
        FilterChips.Clear();

        AddTokenChips(FilterChipKinds.Id, "ID", IdListText, v => IdListText = v);
        AddTokenChips(FilterChipKinds.Pgn, "PGN", PgnText, v => PgnText = v);
        AddTokenChips(FilterChipKinds.Sa, "SA", SaText, v => SaText = v);
        AddTokenChips(FilterChipKinds.Da, "DA", DaText, v => DaText = v);

        if (!string.IsNullOrWhiteSpace(DbcMessageName))
            FilterChips.Add(new FilterChipViewModel(
                $"DBC {DbcMessageName}", FilterChipKinds.Dbc, () => DbcMessageName = ""));

        if (ChannelFilter is { } channel)
            FilterChips.Add(new FilterChipViewModel(
                $"通道 {channel}", FilterChipKinds.Channel, () => ChannelFilter = null));

        if (ShowErrorsOnly)
            FilterChips.Add(new FilterChipViewModel(
                "仅错误帧", FilterChipKinds.ErrorsOnly, () => ShowErrorsOnly = false));

        if (ExcludeMatch)
            FilterChips.Add(new FilterChipViewModel(
                "排除模式", FilterChipKinds.ExcludeMode, () => ExcludeMatch = false));

        if (!string.IsNullOrWhiteSpace(PayloadOffsetText)
            || !string.IsNullOrWhiteSpace(PayloadMaskHex)
            || !string.IsNullOrWhiteSpace(PayloadValueHex))
        {
            FilterChips.Add(new FilterChipViewModel(
                $"Payload {PayloadOffsetText}/{PayloadMaskHex}/{PayloadValueHex}",
                FilterChipKinds.Payload,
                () =>
                {
                    PayloadOffsetText = "";
                    PayloadMaskHex = "";
                    PayloadValueHex = "";
                }));
        }

        // 行右键产生的排除项：单独成 chip，移除即恢复该 ID。
        foreach (var rawId in _excludedIds.ToArray())
        {
            var raw = rawId;
            var label = _excludedIdLabels.TryGetValue(raw, out var text)
                ? text
                : $"0x{raw:X}";
            FilterChips.Add(new FilterChipViewModel(
                $"排除 {label}", FilterChipKinds.ExcludedId, () =>
                {
                    if (_excludedIds.Remove(raw))
                    {
                        _excludedIdLabels.Remove(raw);
                        ApplyExclusionChange();
                    }
                }));
        }
    }

    /// <summary>
    /// 把一个"逗号/空白分隔的 token 列表"字段展开为逐 token chip；
    /// 删除某个 chip 时从原文本中移除该 token 并回写（其余 token 顺序不变）。
    /// </summary>
    private void AddTokenChips(string kind, string prefix, string text, Action<string> assign)
    {
        var tokens = text
            .Split(ChipTokenSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .ToList();
        if (tokens.Count == 0) return;

        for (int i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var index = i; // 闭包捕获循环变量副本（for 的 i 是共享变量）
            FilterChips.Add(new FilterChipViewModel(
                $"{prefix} {token}", kind, () =>
                {
                    var remaining = tokens
                        .Where((_, idx) => idx != index)
                        .ToList();
                    assign(string.Join(", ", remaining));
                }));
        }
    }
}
