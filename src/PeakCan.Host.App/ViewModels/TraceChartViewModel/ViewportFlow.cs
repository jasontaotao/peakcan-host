using PeakCan.Host.App.Services.Trace;
using ScottPlot;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class TraceChartViewModel
{
    // Flow F: ViewportBundle (v3.5.0 MINOR).
    // v3.62.0 MINOR: migrated from OxyPlot LinearAxis → ScottPlot IAxis.Min/Max.

    public IReadOnlyList<BundleViewportDto> CaptureViewports()
    {
        var result = new List<BundleViewportDto>(Series.Count);
        foreach (var s in Series)
        {
            // v12 fix: Plot is null until the View creates its own Plot
            // (common during AI chat before the chart tab is visited).
            if (s.Plot is null) continue;
            // v3.62.0 MINOR: IAxis.Min/Max replaces
            // xAxis.ActualMinimum/ActualMaximum
            var xMin = s.Plot.Axes.Bottom.Min;
            var xMax = s.Plot.Axes.Bottom.Max;
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

    public void ApplyViewports(IEnumerable<BundleViewportDto> viewports)
    {
        ArgumentNullException.ThrowIfNull(viewports);
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
            // v3.62.0 MINOR: SetLimitsX replaces xAxis.Minimum/Maximum
            if (!double.IsNaN(vp.XMin) && !double.IsNaN(vp.XMax))
            {
                cur.Plot.Axes.SetLimitsX(vp.XMin, vp.XMax);
                cur.RefreshCallback?.Invoke();
            }
            var focusOrCollapseChanged = cur.IsFocused != vp.IsFocused || cur.IsCollapsed != vp.IsCollapsed;
            if (focusOrCollapseChanged)
            {
                Series[i] = cur with { IsFocused = vp.IsFocused, IsCollapsed = vp.IsCollapsed };
                changed = true;
            }
        }
        if (changed && (anyFocused || anyCollapsed)) RecomputeHeights();
    }
}
