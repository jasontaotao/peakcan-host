namespace PeakCan.Host.App.ViewModels;

public sealed partial class TraceChartViewModel
{
    // Flow D: FocusCollapse (v3.2.0 MINOR + earlier).
    // Methods moved verbatim from TraceChartViewModel.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - ToggleCollapse -> RecomputeHeights (Flow A, partial file)
    //   - SetFocus -> RecomputeHeights (Flow A, partial file)

    public void ToggleCollapse(TraceChartSeries s)
    {
        // v3.2.0 MINOR: look up by EffectiveKey (SourceId.SignalKey) so the
        // lookup is unambiguous when two traces share a SignalKey. When
        // SourceId is empty (single-trace callers), EffectiveKey falls
        // back to SignalKey — preserves v3.0 test fixture expectations.
        for (int i = 0; i < Series.Count; i++)
        {
            if (Series[i].EffectiveKey == s.EffectiveKey)
            {
                var current = Series[i];
                Series[i] = current with { IsCollapsed = !current.IsCollapsed };
                RecomputeHeights();
                return;
            }
        }
    }

    public void SetFocus(TraceChartSeries s)
    {
        // v3.2.0 MINOR: focus targets one (source, signal) pair — use
        // EffectiveKey so two traces' same-SignalKey series do not both
        // receive focus when only one was clicked.
        var changed = false;
        for (int i = 0; i < Series.Count; i++)
        {
            var cur = Series[i];
            var isFocused = cur.EffectiveKey == s.EffectiveKey;
            if (cur.IsFocused != isFocused)
            {
                Series[i] = cur with { IsFocused = isFocused };
                changed = true;
            }
        }
        if (changed) RecomputeHeights();
    }
}