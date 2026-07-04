using OxyPlot;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// One charted signal in the Trace Viewer. Carries its own
/// <see cref="PlotModel"/> (per-signal subplot) with the
/// <see cref="OxyPlot.Series.LineSeries"/> already populated and the
/// X/Y axes configured. Color is assigned at creation — v3.2.0 MINOR
/// moves palette assignment from per-series (TraceChartViewModel) to
/// per-source (ITracePalette), so all series of a given source share
/// the source's color identity.
/// <para>
/// <b>Multi-trace overlay (v3.2.0 MINOR):</b> <see cref="SourceId"/>
/// disambiguates the same logical signal from multiple traces. When two
/// traces both have a "0x100.RPM" signal, each becomes a separate
/// <see cref="TraceChartSeries"/> with a different <see cref="SourceId"/>.
/// <see cref="EffectiveKey"/> is the lookup key used by
/// <see cref="TraceChartViewModel"/> for remove/toggle/focus (always
/// unique per (source, signal)); <see cref="SignalKey"/> is the logical
/// key for collapse/focus grouping across sources (collides by design).
/// </para>
/// </summary>
public sealed record TraceChartSeries(
    string SignalKey,           // "0x100.EngineRPM" — logical key
    string DisplayName,         // "EngineRPM"
    string Unit,                // "RPM" or "" if DBC not loaded
    OxyColor Color,
    PlotModel PlotModel,
    IReadOnlyList<double> XValues,   // monotonically increasing
    IReadOnlyList<double> YValues,   // decoded physical values
    double MinValue,
    double MaxValue,
    bool IsFocused,
    bool IsCollapsed,
    // v3.2.0 MINOR: empty string for v3.0 single-trace callers; non-empty
    // GUID assigned by TraceSessionRegistry when the series originates
    // from a loaded TraceSource.
    string SourceId = "")
{
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