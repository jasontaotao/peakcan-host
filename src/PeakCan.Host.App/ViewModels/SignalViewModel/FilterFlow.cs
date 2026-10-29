using CommunityToolkit.Mvvm.Input;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class SignalViewModel
{
    // Flow D: Filter/search (v1.2.3 throttle + earlier).
    // Methods moved verbatim from SignalViewModel.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - OnSearchTextChanged -> ApplyFilter (intra-flow)
    //   - ApplyFilter reads SearchText (state, main file), Latest (state, main file),
    //     FilteredSignals (state, main file), _lastFilterPattern + _lastFilterRebuildUtc
    //     + FilterRebuildInterval + FilterRebuildCount (state, main file)
    //   - ApplyFilter is called from Reset (Flow B) and ApplyEntries (Flow B)

    /// <summary>
    /// Called when <see cref="SearchText"/> changes. Filters the
    /// <see cref="FilteredSignals"/> collection.
    /// </summary>
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    /// <summary>
    /// P0 空态里的 [清除搜索]：用户搜出"没有匹配的信号"后一键回到全量。
    /// 只清 <see cref="SearchText"/>，重建统一由 <see cref="OnSearchTextChanged"/>
    /// 触发——不另开一条直接改 <see cref="FilteredSignals"/> 的路径。
    /// </summary>
    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    private void ApplyFilter()
    {
        var pattern = SearchText.AsSpan().Trim();
        var trimmed = pattern.IsEmpty ? "" : pattern.ToString();

        // v1.2.3 throttle: skip the Clear+Add pass when nothing the
        // user-visible output depends on has changed. The first call
        // after construction is never throttled (the FilteredSignals
        // count check protects against a "first call has the right
        // count by accident" false-skip on an empty Latest).
        var now = DateTime.UtcNow;
        if (trimmed == _lastFilterPattern
            && (now - _lastFilterRebuildUtc) < FilterRebuildInterval
            && FilteredSignals.Count == Latest.Count)
        {
            return;
        }

        _lastFilterPattern = trimmed;
        _lastFilterRebuildUtc = now;
        FilterRebuildCount++;

        FilteredSignals.Clear();
        foreach (var e in Latest)
        {
            if (pattern.Length == 0
                || e.Message.AsSpan().Contains(pattern, StringComparison.OrdinalIgnoreCase)
                || e.Signal.AsSpan().Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                FilteredSignals.Add(e);
            }
        }

        // P0 空态判据：每次真正的重建后补发（计数变化不经过属性通知）。
        // 被节流跳过的调用不会改变两个判据：跳过条件已要求计数未变。
        NotifyEmptyStateChanged();
    }

    /// <summary>一条信号都还没解码出来（未加载 DBC / 未连接 / 帧不匹配）。</summary>
    public bool IsSignalEmpty => Latest.Count == 0;

    /// <summary>有信号，但搜索把它们全过滤掉了。</summary>
    public bool IsSignalFilteredEmpty => Latest.Count > 0 && FilteredSignals.Count == 0;

    /// <summary>
    /// 空态判据变了就补发通知。<see cref="Latest"/> / <see cref="FilteredSignals"/>
    /// 的计数变化本身不经过属性通知，而 XAML 的 DataTrigger 只认通知。
    /// </summary>
    private void NotifyEmptyStateChanged()
    {
        OnPropertyChanged(nameof(IsSignalEmpty));
        OnPropertyChanged(nameof(IsSignalFilteredEmpty));
    }
}