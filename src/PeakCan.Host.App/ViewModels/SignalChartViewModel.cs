using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// Backing view model for the real-time signal chart embedded in the
/// Signal tab. Manages per-signal <see cref="LineSeries"/> instances,
/// buffers incoming decoded samples, and drains them to OxyPlot at
/// 30 Hz via a <see cref="DispatcherTimer"/>.
/// <para>
/// <b>Performance:</b> at ~8 kfps the SDK thread pushes one sample per
/// decoded signal per frame. The buffer stores only the latest value
/// per signal between two render ticks (coalescing). At 30 Hz the
/// timer drains the buffer and appends one point per signal to each
/// <see cref="LineSeries"/> — OxyPlot redraws once per tick, not once
/// per frame.
/// </para>
/// <para>
/// <b>Time axis:</b> the first sample defines <c>t = 0</c>. All
/// subsequent timestamps are plotted as seconds-since-first-sample.
/// The X axis auto-scrolls to a rolling <see cref="WindowSeconds"/>
/// window.
/// </para>
/// <para>
/// <b>Threading:</b> all public methods run on the WPF UI thread
/// (callers marshal via <see cref="DispatcherExtensions.RunOnUiPost"/>).
/// The <see cref="DispatcherTimer"/> tick also fires on the UI thread.
/// In test contexts (no <c>Application</c>) the timer is not started;
/// tests call <see cref="DrainBufferForTest"/> directly.
/// </para>
/// <para>
/// <b>No <c>IDisposable</c>:</b> DI singleton that lives for the
/// whole app lifetime; same pattern as
/// <see cref="StatsViewModel"/> and <see cref="SignalViewModel"/>.
/// </para>
/// </summary>
public sealed partial class SignalChartViewModel : ObservableObject
{
    /// <summary>Per-signal statistics for the charted window.</summary>
    public sealed record SignalStatistics(
        string SignalKey,
        string DisplayName,
        double Min,
        double Max,
        double Average,
        int SampleCount);

    /// <summary>Rolling window width in seconds.</summary>
    internal const double WindowSeconds = 30.0;

    /// <summary>Render timer interval in milliseconds (~30 Hz).</summary>
    internal const int RenderIntervalMs = 33;

    /// <summary>Hard cap on points per signal series.</summary>
    internal const int MaxPointsPerSeries = 10_000;

    /// <summary>Tableau 10 color palette for distinct signal lines.</summary>
    private static readonly OxyColor[] Palette =
    {
        OxyColor.FromRgb(0x1F, 0x77, 0xB4),  // blue
        OxyColor.FromRgb(0xFF, 0x7F, 0x0E),  // orange
        OxyColor.FromRgb(0x2C, 0xA0, 0x2C),  // green
        OxyColor.FromRgb(0xD6, 0x27, 0x28),  // red
        OxyColor.FromRgb(0x94, 0x67, 0xBD),  // purple
        OxyColor.FromRgb(0x8C, 0x56, 0x4B),  // brown
        OxyColor.FromRgb(0xE3, 0x77, 0xC2),  // pink
        OxyColor.FromRgb(0x7F, 0x7F, 0x7F),  // gray
        OxyColor.FromRgb(0xBC, 0xBD, 0x22),  // olive
        OxyColor.FromRgb(0x17, 0xBE, 0xCF),  // cyan
    };

    // Per-signal bookkeeping. Key = "Message.Signal".
    private readonly Dictionary<string, LineSeries> _seriesByKey = new();
    private readonly Dictionary<string, string> _displayNames = new();
    private readonly Dictionary<string, int> _colorIndex = new();
    private int _nextColorSlot;

    /// <summary>
    /// Wall-clock offset: the <see cref="Timestamp.TotalMicroseconds"/>
    /// of the first sample. All subsequent X values are relative to
    /// this anchor. Null before the first sample arrives.
    /// </summary>
    private ulong? _t0;

    private DispatcherTimer? _renderTimer;

    /// <summary>OxyPlot model bound to the chart in SignalView.xaml.</summary>
    public PlotModel PlotModel { get; }

    /// <summary>
    /// Whether any signals are currently being charted. Exposed for
    /// test assertions and potential UI state binding.
    /// </summary>
    public bool HasSignals => _seriesByKey.Count > 0;

    /// <summary>Number of signals currently being charted.</summary>
    public int SignalCount => _seriesByKey.Count;

    public SignalChartViewModel()
    {
        PlotModel = new PlotModel
        {
            Title = "Signal chart (30 s rolling window)",
            TitleFontSize = 12,
            PlotAreaBorderColor = OxyColor.FromRgb(0xCC, 0xCC, 0xCC),
        };

        // X axis: relative seconds from first sample.
        PlotModel.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Bottom,
            Minimum = 0,
            Maximum = WindowSeconds,
            MajorStep = 5,
            Title = "Time (s)",
        });

        // Y axis: auto-range per signal value.
        PlotModel.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Left,
            Title = "Value",
        });
    }


    // Buffer for (x, y) pairs — latest per signal per tick. Coalesced:
    // between two render ticks only the last value per signal survives.
    private readonly Dictionary<string, (double x, double y)> _pendingPoints = new();


    // === Flow A methods moved to SignalChartViewModel/SeriesManagementFlow.cs (W21 Task 1) ===


    // === Flow C methods moved to SignalChartViewModel/StatisticsExportFlow.cs (W21 Task 3) ===

    // === Flow B methods moved to SignalChartViewModel/FrameIngestFlow.cs (W21 Task 2) ===
}
