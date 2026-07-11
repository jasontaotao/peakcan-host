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
    }
}