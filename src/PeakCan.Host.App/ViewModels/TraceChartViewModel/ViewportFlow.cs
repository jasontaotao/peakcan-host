using OxyPlot.Axes;
using PeakCan.Host.App.Services.Trace;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class TraceChartViewModel
{
    // Flow F: ViewportBundle (v3.5.0 MINOR).
    // Methods moved verbatim from TraceChartViewModel.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - CaptureViewports -> Series (state, main)
    //   - ApplyViewports -> Series (state, main) + RecomputeHeights (Flow A, partial file)

    /// <summary>
    /// v3.5.0 MINOR: snapshot the per-series X-axis viewport (min/max),
    /// focus, and collapse state for round-trip into a
    /// <see cref="BundleViewportDto"/> list. Called by
    /// <c>TraceViewerViewModel.BuildSnapshot</c> right before
    /// <c>TraceSessionLibrary.Save</c>.
    /// </summary>
    public IReadOnlyList<BundleViewportDto> CaptureViewports()
    {
        var result = new List<BundleViewportDto>(Series.Count);
        foreach (var s in Series)
        {
            var xAxis = s.PlotModel.Axes.OfType<LinearAxis>()
                .FirstOrDefault(a => a.Position == AxisPosition.Bottom);
            if (xAxis is null) continue;
            // ActualMinimum/Maximum reflect the user's pan/zoom, not the
            // raw data range. Use NaN as a "not yet rendered" sentinel —
            // the deserializer writes NaN straight through and the apply
            // path skips axes with NaN bounds.
            var xMin = xAxis.ActualMinimum;
            var xMax = xAxis.ActualMaximum;
            if (double.IsNaN(xMin) || double.IsNaN(xMax)) continue;
            result.Add(new BundleViewportDto
            {
                EffectiveKey = s.EffectiveKey,
                XMin = xMin,
                XMax = xMax,
                IsFocused = s.IsFocused,
                IsCollapsed = s.IsCollapsed,
            });
        }
        return result;
    }

    /// <summary>
    /// v3.5.0 MINOR: restore per-series X-axis viewport + focus/collapse
    /// from a saved <see cref="BundleViewportDto"/> list. MUST run AFTER
    /// <see cref="SyncYAxes"/> (which writes to the Y axis) and AFTER
    /// <see cref="RebuildSignalsCore"/> populates the Series collection —
    /// otherwise the per-axis writes would land on stale or empty
    /// PlotModels. Viewports are matched by <see cref="TraceChartSeries.EffectiveKey"/>
    /// (SourceId.SignalKey) so two traces' same-SignalKey series are
    /// disambiguated.
    /// </summary>
    public void ApplyViewports(IEnumerable<BundleViewportDto> viewports)
    {
        ArgumentNullException.ThrowIfNull(viewports);
        // Group by EffectiveKey so a single key from the bundle maps to
        // exactly one (source, signal) pair. The bundle writer guarantees
        // 1:1 already; the GroupBy.Last() pick is defensive against
        // duplicates -- a hand-edited or producer-bug-crafted bundle with
        // two entries sharing an EffectiveKey no longer crashes
        // ApplyViewports via ToDictionary's duplicate-key throw.
        var byKey = viewports
            .Where(v => !string.IsNullOrEmpty(v.EffectiveKey))
            .GroupBy(v => v.EffectiveKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var anyFocused = byKey.Values.Any(v => v.IsFocused);
        var anyCollapsed = byKey.Values.Any(v => v.IsCollapsed);
        var changed = false;
        for (int i = 0; i < Series.Count; i++)
        {
            var cur = Series[i];
            if (!byKey.TryGetValue(cur.EffectiveKey, out var vp)) continue;
            var xAxis = cur.PlotModel.Axes.OfType<LinearAxis>()
                .FirstOrDefault(a => a.Position == AxisPosition.Bottom);
            if (xAxis is not null && !double.IsNaN(vp.XMin) && !double.IsNaN(vp.XMax))
            {
                xAxis.Minimum = vp.XMin;
                xAxis.Maximum = vp.XMax;
                cur.PlotModel.InvalidatePlot(false);
            }
            var focusOrCollapseChanged = cur.IsFocused != vp.IsFocused || cur.IsCollapsed != vp.IsCollapsed;
            if (focusOrCollapseChanged)
            {
                Series[i] = cur with { IsFocused = vp.IsFocused, IsCollapsed = vp.IsCollapsed };
                changed = true;
            }
        }
        // Recompute AdaptiveHeight — IsFocused/IsCollapsed changed.
        // Skip when neither flag moved anywhere (cheap no-op early return).
        if (changed && (anyFocused || anyCollapsed)) RecomputeHeights();
    }
}