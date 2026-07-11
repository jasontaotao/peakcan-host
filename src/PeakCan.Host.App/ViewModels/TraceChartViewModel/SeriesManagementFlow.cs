namespace PeakCan.Host.App.ViewModels;

public sealed partial class TraceChartViewModel
{
    // Flow A: SeriesManagement (v3.0.2 PATCH + v3.2.0 MINOR + earlier).
    // Methods + 1 internal static helper moved verbatim from TraceChartViewModel.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - AddSeries -> Series (state, main) + RecomputeHeights (intra-flow)
    //   - RemoveSeries -> Series (state, main) + RecomputeHeights (intra-flow)
    //   - RecomputeHeights -> Series (state, main) + Compute (intra-flow) + _chartAreaHeight (state, main)
    //   - Compute -> MinSubplotHeight / MaxSubplotHeight / CollapsedSubplotHeight (constants, main)
    //
    // Cross-partial callers:
    //   - ChartAreaHeight.set (main) -> RecomputeHeights (this partial)
    //   - ToggleCollapse (Flow D, partial) -> RecomputeHeights (this partial)
    //   - SetFocus (Flow D, partial) -> RecomputeHeights (this partial)
    //   - ApplyViewports (Flow F, partial) -> RecomputeHeights (this partial)

    public void AddSeries(TraceChartSeries s)
    {
        Series.Add(s);
        RecomputeHeights();
    }

    public void RemoveSeries(TraceChartSeries s)
    {
        // v3.2.0 MINOR: match by EffectiveKey (SourceId.SignalKey) so the
        // lookup is unambiguous when two traces share a SignalKey. When
        // SourceId is empty (single-trace callers), EffectiveKey falls
        // back to SignalKey — preserves v3.0 test fixture expectations.
        for (int i = 0; i < Series.Count; i++)
        {
            if (Series[i].EffectiveKey == s.EffectiveKey)
            {
                Series.RemoveAt(i);
                RecomputeHeights();
                return;
            }
        }
    }

    /// <summary>
    /// Re-runs <see cref="Compute"/> for every series and writes the
    /// result back as a new record (via the <c>with</c> expression) so
    /// XAML bindings see the updated <c>AdaptiveHeight</c>.
    /// </summary>
    public void RecomputeHeights()
    {
        var visibleCount = Series.Count(s => !s.IsCollapsed);
        var focusModeActive = Series.Any(s => s.IsFocused);
        for (int i = 0; i < Series.Count; i++)
        {
            var cur = Series[i];
            var newHeight = Compute(cur, visibleCount, focusModeActive, _chartAreaHeight);
            if (cur.AdaptiveHeight != newHeight)
                Series[i] = cur with { AdaptiveHeight = newHeight };
        }
    }

    /// <summary>
    /// Spec §3 adaptive-subplot-height algorithm.
    /// <list type="bullet">
    ///   <item>Collapsed series: <c>24</c> px (header-only).</item>
    ///   <item>Focused series (any focus active): <c>clamp(H * 0.5, 80, 250)</c>.</item>
    ///   <item>Non-focused (in focus mode): <c>clamp((H * 0.5) / max(N-1, 1), 80, 250)</c>.</item>
    ///   <item>General case (no focus): <c>clamp(H / N, 80, 250)</c>.</item>
    /// </list>
    /// <paramref name="visibleCount"/> is the number of non-collapsed series.
    /// <paramref name="focusModeActive"/> is true iff at least one series in
    /// the collection has <c>IsFocused == true</c>.
    /// </summary>
    internal static double Compute(TraceChartSeries s, int visibleCount, bool focusModeActive, double h)
    {
        if (s.IsCollapsed) return CollapsedSubplotHeight;
        if (s.IsFocused) return Math.Clamp(h * 0.5, MinSubplotHeight, MaxSubplotHeight);
        if (focusModeActive)
        {
            // Others share the remaining half. visibleCount includes this
            // (non-collapsed, non-focused) series; the focused series is
            // already excluded from the visible share.
            var divisor = Math.Max(visibleCount - 1, 1);
            return Math.Clamp((h * 0.5) / divisor, MinSubplotHeight, MaxSubplotHeight);
        }
        // Equal share: H / N, clamped.
        if (visibleCount <= 0) return MinSubplotHeight;
        return Math.Clamp(h / visibleCount, MinSubplotHeight, MaxSubplotHeight);
    }
}