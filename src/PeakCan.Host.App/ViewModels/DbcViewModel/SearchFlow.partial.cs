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
        // Clear 会让 DataGrid 通过 TwoWay 绑定把 SelectedMessage 置空。
        // 先记住旧选中行与 mux 分支；过滤/恢复期间抑制布局重算，最后最多重算一次。
        var selected = SelectedMessage;
        var selectedMux = SelectedMuxValue;
        var wasSuppressed = _suppressLayout;
        _suppressLayout = true;
        try
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

            if (selected is not null)
            {
                SelectedMessage = FilteredMessages.FirstOrDefault(
                    m => string.Equals(m.Id, selected.Id, StringComparison.Ordinal)
                         && string.Equals(m.Name, selected.Name, StringComparison.Ordinal));
            }
        }
        finally
        {
            _suppressLayout = wasSuppressed;
        }

        if (SelectedMessage is null)
        {
            // 选中被过滤掉：清空位图。原本就没有选中时无需动作。
            if (selected is not null) RebuildMuxAndLayout();
        }
        else if (!ReferenceEquals(SelectedMessage, selected))
        {
            // 恢复到另一个实例（例如 DBC 内重复 Id+Name，或列表重建换了 VM 实例）：
            // 按新消息重建，尽量保留 mux。
            RebuildMuxAndLayout(selectedMux);
        }
        // 同一实例：保留现有快照与 mux，避免每个按键都重建全部格子。

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
