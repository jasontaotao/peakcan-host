using CommunityToolkit.Mvvm.ComponentModel;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// One row in the left-side Trace Viewer signal table. Created from the
/// loaded DBC; columns update as the user loads/unloads .asc files.
/// <para>
/// v3.14.3 PATCH: class (was record) so <see cref="IsPlotted"/>,
/// <see cref="FrameCount"/>, and <see cref="LatestValue"/> are INPC-bindable.
/// </para>
/// <para>
/// v3.14.3 PATCH: <see cref="LatestValue"/> is set once at build time
/// from the last matching frame in the source bucket, and re-set when
/// the user reloads an .asc. It does NOT update as the playback cursor
/// moves — playback updates only the chart subplot's cursor annotation.
/// (The pre-v3.14.3 xmldoc claim was incorrect.)
/// </para>
/// </summary>
public sealed partial class TraceSignalRow : ObservableObject
{
    public string CanIdHex { get; init; }
    public string MessageName { get; init; }
    public string SignalName { get; init; }
    public string Unit { get; init; }

    [ObservableProperty]
    private bool _isPlotted;

    [ObservableProperty]
    private int _frameCount;

    [ObservableProperty]
    private double _latestValue;

    public TraceSignalRow(
        string canIdHex,
        string messageName,
        string signalName,
        string unit,
        bool isPlotted = false,
        int frameCount = 0,
        double latestValue = double.NaN)
    {
        CanIdHex = canIdHex;
        MessageName = messageName;
        SignalName = signalName;
        Unit = unit;
        _isPlotted = isPlotted;
        _frameCount = frameCount;
        _latestValue = latestValue;
    }

    /// <summary>v3.14.3 PATCH: lookup key for chart-side operations.
    /// Matches <see cref="TraceChartSeries.SignalKey"/> format
    /// ("{idHex}.{signalName}").</summary>
    public string SignalKey => $"{CanIdHex}.{SignalName}";
}