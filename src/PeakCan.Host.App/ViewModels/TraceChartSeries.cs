using OxyPlot;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// One charted signal in the Trace Viewer. Carries its own
/// <see cref="PlotModel"/> (per-signal subplot) with the
/// <see cref="OxyPlot.Series.LineSeries"/> already populated and the
/// X/Y axes configured. Color is assigned at creation from the shared
/// 10-color palette by <see cref="TraceChartViewModel"/>.
/// </summary>
public sealed record TraceChartSeries(
    string SignalKey,           // "0x100.EngineRPM" — unique
    string DisplayName,         // "EngineRPM"
    string Unit,                // "RPM" or "" if DBC not loaded
    OxyColor Color,
    PlotModel PlotModel,
    IReadOnlyList<double> XValues,   // monotonically increasing
    IReadOnlyList<double> YValues,   // decoded physical values
    double MinValue,
    double MaxValue,
    bool IsFocused,
    bool IsCollapsed);
