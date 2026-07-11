using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using OxyPlot;
using OxyPlot.Axes;
using PeakCan.Host.App.Services.Trace;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class TraceChartViewModel : ObservableObject
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

    // v3.16.9 PATCH: throttling state for UpdatePlaybackCursor. The
    // playback timer fires every 1 ms (ReplayTimeline.OnTick period=1),
    // but OxyPlot.PlotModel.InvalidatePlot on every 1 ms call causes
    // the WPF window to freeze (the layout pass for the chart cannot
    // keep up with 1000 plot invalidations / second per series).
    // Throttle to one update per ~16 ms (60 fps) — the human eye cannot
    // distinguish 60 fps cursor motion from 1000 fps cursor motion, and
    // 60 fps is the WPF default render cadence.
    // v3.16.9.1 PATCH (code-review H1): use Stopwatch ticks (monotonic,
    // high-resolution, immune to wall-clock NTP/clock-jump adjustments)
    // instead of DateTime.UtcNow. The project already uses Stopwatch in
    // RateLimitedSendService.cs:50,105,110,130 — using it here is
    // consistent with existing patterns. DateTime.UtcNow would silently
    // disable the throttle on a clock-jump backward, re-creating the
    // original freeze bug.
    // v3.16.9.1 PATCH: sentinel value for "never invalidated". Must be 0
    // (not long.MinValue) because (Stopwatch.GetTimestamp() - long.MinValue)
    // overflows to a NEGATIVE number on the first call, which would
    // cause elapsedMs < 16 to be FALSE (negative number is < 16 is true,
    // so the throttle would skip the first invalidate). 0 means
    // "uninitialized" — the first call's elapsedMs will be a large
    // positive number (current ticks - 0), and the throttle will allow
    // the first invalidate.
    private long _lastCursorInvalidateTicks = 0L;
    private double _lastCursorX = double.NaN;
    private const double CursorInvalidateIntervalMs = 16.0;
    private static readonly double StopwatchTicksToMs = 1000.0 / Stopwatch.Frequency;
    // v3.16.9.1 PATCH (code-review M2): test hook. The throttle test
    // needs an observable InvalidatePlot call count; without this,
    // removing the throttle would still pass the test (false-positive
    // green). Counter increments inside the foreach so per-series
    // invalidations are summed.
    [ObservableProperty]
    private int _invalidatePlotCallCount;

    public void UpdatePlaybackCursor(double x)
    {
        PlaybackCursorX = x;
        // Skip the actual InvalidatePlot call if either:
        // (a) the new X is the same as the last-rendered X (no movement),
        //     which happens when OnTick emits multiple frames at the
        //     same timestamp (rounded values), or
        // (b) less than 16 ms (Stopwatch ticks) have passed since the
        //     last invalidate. Using Stopwatch (not DateTime) avoids
        //     wall-clock-jump disarming the throttle.
        // Without (a), a duplicate-timestamp frame burst would burn the
        // full 1000 fps invalidate rate. Without (b), the user's
        // window freezes mid-playback. Both are empirical findings from
        // v3.16.9 PATCH user reproduction: "clicked Play and the window
        // froze" — root-caused to UpdatePlaybackCursor invalidating at
        // the timer cadence (1 ms) instead of the render cadence (16 ms).
        var nowTicks = Stopwatch.GetTimestamp();
        var elapsedMs = (nowTicks - _lastCursorInvalidateTicks) * StopwatchTicksToMs;
        if (x == _lastCursorX || elapsedMs < CursorInvalidateIntervalMs)
            return;
        _lastCursorInvalidateTicks = nowTicks;
        _lastCursorX = x;
        foreach (var s in Series)
        {
            var cursor = s.PlotModel.Annotations.OfType<OxyPlot.Annotations.LineAnnotation>()
                .FirstOrDefault(a => a.Tag as string == "playback-cursor");
            if (cursor != null)
            {
                cursor.X = x;
                s.PlotModel.InvalidatePlot(false);
                InvalidatePlotCallCount++;
            }
        }
    }

    public void SetTotalDuration(double seconds) => TotalDuration = seconds;



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

    /// <summary>
    /// v3.3.2 PATCH: cross-source Y-axis auto-scale coordination. Groups
    /// subplots by logical <see cref="TraceChartSeries.SignalKey"/> (NOT
    /// <see cref="TraceChartSeries.EffectiveKey"/> — SourceId-qualified —
    /// because we want all sources of the same signal to share one Y axis)
    /// and sets each group's Y axis (Left <see cref="LinearAxis"/>) to the
    /// union min/max of all sources' Y data, with 5% padding for visual
    /// breathing room.
    /// <para>
    /// <b>Forward-looking:</b> v3.3.2 ships this method as a stand-alone,
    /// testable unit. Production wiring (calling this from
    /// <c>TraceViewerViewModel.RebuildSignalsAsync</c> after the chart
    /// series construction is itself unblocked) is deferred to v3.4.0.
    /// </para>
    /// </summary>
    public void SyncYAxes()
    {
        const double PaddingFraction = 0.05;
        foreach (var group in Series.GroupBy(s => s.SignalKey))
        {
            double min = double.PositiveInfinity, max = double.NegativeInfinity;
            bool hasData = false;
            foreach (var s in group)
            {
                foreach (var y in s.YValues)
                {
                    if (y < min) min = y;
                    if (y > max) max = y;
                    hasData = true;
                }
            }
            if (!hasData) continue;
            var range = max - min;
            var pad = range * PaddingFraction;
            if (pad == 0.0) pad = Math.Max(Math.Abs(max) * PaddingFraction, 1e-9);
            var yMin = min - pad;
            var yMax = max + pad;
            foreach (var s in group)
            {
                var yAxis = s.PlotModel.Axes.OfType<LinearAxis>()
                    .FirstOrDefault(a => a.Position == AxisPosition.Left);
                if (yAxis is null) continue;
                yAxis.Minimum = yMin;
                yAxis.Maximum = yMax;
                s.PlotModel.InvalidatePlot(false);
            }
        }
    }

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
    // === Flow D methods moved to TraceChartViewModel/FocusCollapseFlow.cs (W8 Task 1) ===
    // === Flow C methods moved to TraceChartViewModel/StatisticsFlow.cs (W8 Task 2) ===
}
