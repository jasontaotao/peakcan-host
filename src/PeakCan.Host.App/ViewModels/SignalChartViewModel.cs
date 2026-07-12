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

    /// <summary>
    /// Add a signal to the chart. Creates a new <see cref="LineSeries"/>
    /// with the next palette color. No-op if the signal is already
    /// charted.
    /// </summary>
    /// <param name="signalKey">Unique key ("Message.Signal").</param>
    /// <param name="displayName">Legend label (signal name).</param>
    public void AddSignal(string signalKey, string displayName)
    {
        if (_seriesByKey.ContainsKey(signalKey)) return;

        var color = Palette[_nextColorSlot % Palette.Length];
        _colorIndex[signalKey] = _nextColorSlot;
        _nextColorSlot++;

        var series = new LineSeries
        {
            Title = displayName,
            Color = color,
            StrokeThickness = 1.5,
            LineJoin = LineJoin.Round,
        };

        _seriesByKey[signalKey] = series;
        _displayNames[signalKey] = displayName;
        PlotModel.Series.Add(series);
        EnsureTimer();
        PlotModel.InvalidatePlot(false);
    }

    /// <summary>
    /// Remove a signal from the chart. No-op if the key is unknown.
    /// </summary>
    public void RemoveSignal(string signalKey)
    {
        if (!_seriesByKey.TryGetValue(signalKey, out var series)) return;

        PlotModel.Series.Remove(series);
        _seriesByKey.Remove(signalKey);
        _displayNames.Remove(signalKey);
        _pendingPoints.Remove(signalKey);
        _colorIndex.Remove(signalKey);

        if (_seriesByKey.Count == 0)
        {
            StopTimer();
            _t0 = null;
        }

        PlotModel.InvalidatePlot(false);
    }

    // Buffer for (x, y) pairs — latest per signal per tick. Coalesced:
    // between two render ticks only the last value per signal survives.
    private readonly Dictionary<string, (double x, double y)> _pendingPoints = new();

    /// <summary>
    /// Append a decoded sample for the given signal. The value is
    /// buffered and rendered on the next timer tick.
    /// <para>
    /// <b>Coalescing:</b> between two render ticks only the latest
    /// value per signal is kept. At ~8 kfps this discards ~265
    /// intermediate values per 33 ms window — visually
    /// indistinguishable on a 30-second chart.
    /// </para>
    /// </summary>
    /// <param name="signalKey">Unique key ("Message.Signal").</param>
    /// <param name="physicalValue">Decoded engineering value.</param>
    /// <param name="timestampMicroseconds">
    /// Frame timestamp in microseconds (monotonic).
    /// </param>
    public void AppendSample(string signalKey, double physicalValue, ulong timestampMicroseconds)
    {
        if (!_seriesByKey.ContainsKey(signalKey)) return;

        _t0 ??= timestampMicroseconds;
        var relativeSeconds = (double)(timestampMicroseconds - _t0.Value) / 1_000_000.0;

        // Overwrite: only the latest value per signal survives between ticks.
        _pendingPoints[signalKey] = (relativeSeconds, physicalValue);
    }

    /// <summary>
    /// Remove all signals and clear the chart. Called on DBC reload.
    /// </summary>
    public void Reset()
    {
        StopTimer();
        PlotModel.Series.Clear();
        _seriesByKey.Clear();
        _displayNames.Clear();
        _colorIndex.Clear();
        _pendingPoints.Clear();
        _t0 = null;
        _nextColorSlot = 0;
        PlotModel.InvalidatePlot(false);
    }

    /// <summary>
    /// Get statistics for all charted signals. Returns min, max, avg,
    /// and sample count for each signal based on the current chart data.
    /// </summary>
    public IReadOnlyList<SignalStatistics> GetStatistics()
    {
        var result = new List<SignalStatistics>(_seriesByKey.Count);
        foreach (var (key, series) in _seriesByKey)
        {
            if (series.Points.Count == 0)
            {
                result.Add(new SignalStatistics(key, _displayNames.GetValueOrDefault(key, key),
                    double.NaN, double.NaN, double.NaN, 0));
                continue;
            }

            var min = double.MaxValue;
            var max = double.MinValue;
            var sum = 0.0;
            var count = series.Points.Count;

            foreach (var pt in series.Points)
            {
                if (pt.Y < min) min = pt.Y;
                if (pt.Y > max) max = pt.Y;
                sum += pt.Y;
            }

            result.Add(new SignalStatistics(
                key, _displayNames.GetValueOrDefault(key, key),
                min, max, sum / count, count));
        }
        return result;
    }

    /// <summary>
    /// Export all charted signal data to a CSV file. The first column
    /// is "Time (s)", followed by one column per signal.
    /// </summary>
    /// <param name="filePath">Output file path.</param>
    public void ExportToCsv(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        var signals = _seriesByKey.ToList();
        if (signals.Count == 0) return;

        var sb = new StringBuilder();
        // Header
        sb.Append("Time (s)");
        foreach (var (key, _) in signals)
            sb.Append(',').Append(_displayNames.GetValueOrDefault(key, key));
        sb.AppendLine();

        // Collect all unique X values across all series.
        var allX = new SortedSet<double>();
        foreach (var (_, series) in signals)
            foreach (var pt in series.Points)
                allX.Add(pt.X);

        // Build a lookup for each signal's points.
        var lookup = new Dictionary<string, Dictionary<double, double>>(signals.Count);
        foreach (var (key, series) in signals)
        {
            var dict = new Dictionary<double, double>(series.Points.Count);
            foreach (var pt in series.Points)
                dict[pt.X] = pt.Y;
            lookup[key] = dict;
        }

        // Write rows.
        foreach (var x in allX)
        {
            sb.Append(x.ToString("F3", CultureInfo.InvariantCulture));
            foreach (var (key, _) in signals)
            {
                sb.Append(',');
                if (lookup[key].TryGetValue(x, out var y))
                    sb.Append(y.ToString("G", CultureInfo.InvariantCulture));
            }
            sb.AppendLine();
        }

        File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
    }

    /// <summary>
    /// Drain the sample buffer and append points to the
    /// <see cref="LineSeries"/>. Called by the render timer tick.
    /// Exposed as <c>internal</c> for direct invocation in tests.
    /// </summary>
    internal void DrainBufferForTest() => OnRenderTick(null, EventArgs.Empty);

    private void OnRenderTick(object? sender, EventArgs e)
    {
        if (_pendingPoints.Count == 0) return;

        // Snapshot and clear the buffer.
        var snapshot = new List<(string key, double x, double y)>(_pendingPoints.Count);
        foreach (var (key, (x, y)) in _pendingPoints)
            snapshot.Add((key, x, y));
        _pendingPoints.Clear();

        foreach (var (key, x, y) in snapshot)
        {
            if (!_seriesByKey.TryGetValue(key, out var series)) continue;

            series.Points.Add(new DataPoint(x, y));

            // Trim oldest points if over cap.
            while (series.Points.Count > MaxPointsPerSeries)
                series.Points.RemoveAt(0);
        }

        // Auto-scroll X axis to the rolling window.
        var now = snapshot.Max(s => s.x);
        var xAxis = PlotModel.Axes.FirstOrDefault(a => a.Position == AxisPosition.Bottom);
        if (xAxis != null)
        {
            xAxis.Minimum = Math.Max(0, now - WindowSeconds);
            xAxis.Maximum = Math.Max(WindowSeconds, now);
        }

        PlotModel.InvalidatePlot(true);
    }

    private void EnsureTimer()
    {
        if (_renderTimer != null) return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return; // test context — no timer

        _renderTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(RenderIntervalMs),
            DispatcherPriority.Normal,
            OnRenderTick,
            dispatcher);
    }

    private void StopTimer()
    {
        if (_renderTimer is null) return;
        _renderTimer.Stop();
        _renderTimer = null;
    }
}
