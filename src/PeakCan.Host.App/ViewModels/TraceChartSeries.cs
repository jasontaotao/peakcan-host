using ScottPlot;
using PeakCan.Host.App.Services.Trace;
using PeakCan.HIL.Core.Dbc;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// One charted signal in the Trace Viewer.
/// v3.62.0 MINOR: migrated from OxyPlot. The View creates and owns the Plot;
/// this record carries the data needed to populate it (source, signal, color, etc.).
/// Color is assigned at creation — v3.2.0 MINOR
/// moves palette assignment from per-series (TraceChartViewModel) to
/// per-source (ITracePalette), so all series of a given source share
/// the source's color identity.
/// </summary>
public sealed record TraceChartSeries(
    string SignalKey,           // "0x100.EngineRPM" — logical key
    string DisplayName,         // "EngineRPM"
    string Unit,                // "RPM" or "" if DBC not loaded
    Color Color,                // v3.62.0: ScottPlot.Color (was OxyColor)
    Plot? Plot,                 // v3.62.0: usually null; View creates its own Plot
    IReadOnlyList<double> XValues,   // monotonically increasing timestamps
    IReadOnlyList<double> YValues,   // decoded physical values (empty until progressive fill)
    double MinValue,
    double MaxValue,
    bool IsFocused,
    bool IsCollapsed,
    bool IsPlotPending = false,
    string SourceId = "",
    // v3.62.0: data needed by View to populate the WpfPlot's Plot
    TraceSource? Source = null,
    Signal? Signal = null,
    ProgressiveScatterSource? ProgressiveSource = null)
{
    // v3.62.0 MINOR: callback set by the View code-behind when the WpfPlot
    // control is materialized. The VM calls RefreshCallback?.Invoke() to
    // trigger a re-render after mutating the Plot (moving anchor lines,
    // toggling playback cursor). Replaces OxyPlot's InvalidatePlot(false).
    public Action? RefreshCallback { get; set; }

    /// <summary>
    /// v3.2.0 MINOR: unique lookup key for chart-internal operations
    /// (remove, toggle, focus, height recompute). When <see cref="SourceId"/>
    /// is empty (single-trace legacy callers), falls back to <see cref="SignalKey"/>
    /// so existing v3.0 tests and fixtures continue to match by SignalKey.
    /// </summary>
    public string EffectiveKey =>
        string.IsNullOrEmpty(SourceId) ? SignalKey : $"{SourceId}.{SignalKey}";

    /// <summary>
    /// Computed per-instance subplot height in pixels. Bound from XAML
    /// onto <c>PlotView.Height</c>. Updated by
    /// <see cref="TraceChartViewModel"/> whenever <c>ChartAreaHeight</c>,
    /// the series set, or any series' <c>IsFocused</c>/<c>IsCollapsed</c>
    /// flag changes. See spec §3 adaptive-subplot-height algorithm.
    /// </summary>
    public double AdaptiveHeight { get; init; } = 160.0;
}
