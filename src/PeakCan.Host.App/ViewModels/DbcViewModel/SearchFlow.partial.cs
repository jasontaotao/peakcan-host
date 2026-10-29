using CommunityToolkit.Mvvm.Input;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class DbcViewModel
{
    // Flow: Search/filter (SearchText-driven FilteredMessages rebuild).
    // Methods moved verbatim from DbcViewModel.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - OnSearchTextChanged -> ApplyFilter (intra-flow)
    //   - OnLoaded (Flow A) -> ApplyFilter (cross-flow post-load rebuild)
    //   - ApplyFilter -> _allMessages + FilteredMessages (main fields) + SearchText (main [ObservableProperty])

    /// <summary>
    /// Called when <see cref="SearchText"/> changes. Filters the
    /// <see cref="FilteredMessages"/> collection.
    /// </summary>
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        FilteredMessages.Clear();
        var pattern = SearchText.Trim();
        foreach (var m in _allMessages)
        {
            if (pattern.Length == 0
                || m.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase)
                || m.Sender.Contains(pattern, StringComparison.OrdinalIgnoreCase)
                || m.Signals.Any(s => s.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
            {
                FilteredMessages.Add(m);
            }
        }
        NotifyEmptyStateChanged();
    }

    /// <summary>P0 空态：一个 DBC 消息都没加载（首次进入 / 加载前）。</summary>
    public bool HasNoMessages => _allMessages.Count == 0;

    /// <summary>
    /// P0 空态：已经加载了消息，但搜索把它们全挡掉了。此时必须给"清除搜索"的出路，
    /// 否则用户只能自己回想改了哪些条件（与 Trace/Signal 空态同一口径）。
    /// </summary>
    public bool IsSearchEmpty => _allMessages.Count > 0 && FilteredMessages.Count == 0;

    /// <summary>空态里的动作：清掉搜索框，表格立刻恢复全量。</summary>
    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    private void NotifyEmptyStateChanged()
    {
        OnPropertyChanged(nameof(HasNoMessages));
        OnPropertyChanged(nameof(IsSearchEmpty));
    }
}
