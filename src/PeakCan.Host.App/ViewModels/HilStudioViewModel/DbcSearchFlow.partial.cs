namespace PeakCan.Host.App.ViewModels;

public sealed partial class HilStudioViewModel
{
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    /// <summary>
    /// 全量重建过滤集合。匹配 Message.Name / Sender / Signal.Name（结构化, OrdinalIgnoreCase）。
    /// 注意（约束 #6）：与 DbcViewModel 不同, 这里匹配的是结构化 Signal.Name,
    /// 不是含 bit/scale 的格式化串 —— 行为不等价, 是有意改进。
    /// </summary>
    private void ApplyFilter()
    {
        FilteredMessages.Clear();
        var pattern = SearchText.Trim();
        foreach (var m in _allMessages)
        {
            if (pattern.Length == 0
                || m.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase)
                || m.Sender.Contains(pattern, StringComparison.OrdinalIgnoreCase)
                || m.Signals.Any(s => s.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
            {
                FilteredMessages.Add(m);
            }
        }
    }
}
