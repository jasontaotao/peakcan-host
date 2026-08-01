namespace PeakCan.Host.App.ViewModels;

public sealed partial class HilStudioViewModel
{
    /// <summary>全量重建过滤集合。Task 3 增强为按 SearchText 过滤；本任务先全显示。</summary>
    private void ApplyFilter()
    {
        FilteredMessages.Clear();
        foreach (var m in _allMessages)
            FilteredMessages.Add(m);
    }
}
