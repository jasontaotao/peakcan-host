using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using OxyPlot;
using OxyPlot.Axes;

namespace PeakCan.Host.App.ViewModels;

public sealed class TraceChartViewModel : ObservableObject
{
    /// <summary>One statistics entry per charted signal.</summary>
    public sealed record TraceChartStatistics(
        string SignalKey, double Min, double Max, double Average, int SampleCount);

    // DELETED (v3.3.1 PATCH dead code sweep):
    // - internal static readonly OxyColor[] Palette
    // - private int _nextColorSlot
    // Both were remnants from pre-v3.2.0 chart series construction. After
    // v3.2.0 moved palette assignment to ITracePalette, neither is read.
    // v3.3.0 release notes deferred this extraction; v3.3.1 PATCH closes it.

    private double _playbackCursorX;
    private double _totalDuration;
    private double _chartAreaHeight = 800.0;

    // v3.0.2 PATCH Task 1: subplot height clamp bounds (spec §3).
    private const double MinSubplotHeight = 80.0;
    private const double MaxSubplotHeight = 250.0;
    private const double CollapsedSubplotHeight = 24.0;

    public ObservableCollection<TraceChartSeries> Series { get; } = new();
    public double PlaybackCursorX
    {
        get => _playbackCursorX;
        set => SetProperty(ref _playbackCursorX, value);
    }
    public double TotalDuration
    {
        get => _totalDuration;
        set => SetProperty(ref _totalDuration, value);
    }

    /// <summary>
    /// Height (px) of the chart area that hosts the stacked subplots.
    /// Set from the chart area's <c>ActualHeight</c> by the code-behind
    /// (XAML wiring is Task 2 of v3.0.2). When this value changes,
    /// every series' <see cref="TraceChartSeries.AdaptiveHeight"/> is
    /// recomputed via the spec's algorithm.
    /// </summary>
    public double ChartAreaHeight
    {
        get => _chartAreaHeight;
        set
        {
            if (SetProperty(ref _chartAreaHeight, value))
                RecomputeHeights();
        }
    }

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

    public void UpdatePlaybackCursor(double x)
    {
        PlaybackCursorX = x;
        // Re-position red LineAnnotation on every subplot
        foreach (var s in Series)
        {
            var cursor = s.PlotModel.Annotations.OfType<OxyPlot.Annotations.LineAnnotation>()
                .FirstOrDefault(a => a.Tag as string == "playback-cursor");
            if (cursor != null)
            {
                cursor.X = x;
                s.PlotModel.InvalidatePlot(false);
            }
        }
    }

    public void SetTotalDuration(double seconds) => TotalDuration = seconds;

    public IEnumerable<TraceChartStatistics> GetStatistics()
    {
        foreach (var s in Series)
        {
            if (s.YValues.Count == 0)
            {
                yield return new TraceChartStatistics(s.SignalKey, double.NaN, double.NaN, double.NaN, 0);
                continue;
            }
            var min = s.YValues.Min();
            var max = s.YValues.Max();
            var avg = s.YValues.Average();
            yield return new TraceChartStatistics(s.SignalKey, min, max, avg, s.YValues.Count);
        }
    }

    public void ExportToCsv(string filePath)
    {
        if (Series.Count == 0) return;
        var sb = new StringBuilder();
        sb.Append("Time (s)");
        foreach (var s in Series) sb.Append(',').Append(s.DisplayName);
        sb.AppendLine();
        var allX = Series.SelectMany(s => s.XValues).Distinct().OrderBy(x => x).ToList();
        var lookups = Series.ToDictionary(s => s.SignalKey, s =>
        {
            var dict = new Dictionary<double, double>(s.XValues.Count);
            for (int i = 0; i < s.XValues.Count; i++) dict[s.XValues[i]] = s.YValues[i];
            return dict;
        });
        foreach (var x in allX)
        {
            sb.Append(x.ToString("F3", CultureInfo.InvariantCulture));
            foreach (var s in Series)
            {
                sb.Append(',');
                if (lookups[s.SignalKey].TryGetValue(x, out var y))
                    sb.Append(y.ToString("G", CultureInfo.InvariantCulture));
            }
            sb.AppendLine();
        }
        File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
    }

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

    /// <summary>Called by subplot's X-axis when user zooms/pans. Syncs all others.</summary>
    public void SyncXAxis(double minimum, double maximum)
    {
        foreach (var s in Series)
        {
            var xAxis = s.PlotModel.Axes.OfType<LinearAxis>()
                .FirstOrDefault(a => a.Position == AxisPosition.Bottom);
            if (xAxis != null && (xAxis.ActualMinimum != minimum || xAxis.ActualMaximum != maximum))
            {
                xAxis.Minimum = minimum;
                xAxis.Maximum = maximum;
                s.PlotModel.InvalidatePlot(false);
            }
        }
    }
}
