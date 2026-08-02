namespace PeakCan.Host.App.ViewModels;

public sealed partial class HilStudioViewModel
{
    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
        UpdateVisibleSignals();
    }

    /// <summary>
    /// 重建选中消息的信号 grid。搜索激活时只保留 Name 匹配的信号，
    /// 解决"搜信号关键字却显示整个 message 所有信号"的问题。
    /// </summary>
    private void UpdateVisibleSignals()
    {
        VisibleSignals.Clear();
        if (SelectedMessage is not { } msg) return;
        var pattern = SearchText.Trim();
        foreach (var s in msg.Signals)
        {
            if (pattern.Length == 0 || s.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                VisibleSignals.Add(s);
        }
    }

    /// <summary>
    /// 全量重建过滤集合。匹配 Message.Name / Sender / Signal.Name（结构化, OrdinalIgnoreCase）。
    /// 注意（约束 #6）：与 DbcViewModel 不同, 这里匹配的是结构化 Signal.Name,
    /// 不是含 bit/scale 的格式化串 —— 行为不等价, 是有意改进。
    /// </summary>
    private void ApplyFilter()
    {
        // DataGrid 收到集合 Reset 时会经 TwoWay 把 SelectedMessage 写回 null;
        // 重建后恢复选中引用, 避免"搜中当前消息但信号面板被清空"。仅当选中项仍在结果中才恢复。
        var keepMessage = SelectedMessage;
        var keepSignal = SelectedSignal;
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
        SelectedMessage = keepMessage is not null && FilteredMessages.Contains(keepMessage) ? keepMessage : null;
        SelectedSignal = keepSignal is not null && VisibleSignals.Contains(keepSignal) ? keepSignal : null;
    }
}
