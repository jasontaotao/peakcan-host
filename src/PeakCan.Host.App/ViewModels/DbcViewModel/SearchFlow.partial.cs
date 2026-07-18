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
    }
}
