namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// P0 空态判据。
/// <para>
/// 为什么不直接在 XAML 里绑 <c>DataGrid.Items.Count</c> 的 <c>ElementName</c>：
/// Style/DataTrigger 里的 ElementName 解析依赖名字域细节，一旦失效空态会安静地
/// 永不出现（没有报错、没有异常）；这里的布尔值可以在 VM 测试里直接断言。
/// </para>
/// </summary>
public sealed partial class TraceViewModel
{
    /// <summary>一条帧都没有（首次进入、或用户清空后）。</summary>
    public bool IsTraceEmpty => Entries.Count == 0;

    /// <summary>收到过帧，但当前过滤条件一条都不放行——空态必须给"清除过滤"的出路。</summary>
    public bool IsTraceFilteredEmpty => Entries.Count > 0 && EntriesView.Count == 0;

    /// <summary>
    /// 计数变化（<see cref="Entries"/> / <see cref="EntriesView"/>）不经过属性通知，
    /// 由状态文本刷新点统一补发——那里是每次入列/过滤/清空都会走到的汇合点。
    /// </summary>
    private void NotifyEmptyStateChanged()
    {
        OnPropertyChanged(nameof(IsTraceEmpty));
        OnPropertyChanged(nameof(IsTraceFilteredEmpty));
    }
}
