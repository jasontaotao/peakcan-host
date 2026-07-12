using System.Windows;
using System.Windows.Threading;
using OxyPlot;
using OxyPlot.Axes;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class SignalChartViewModel
{
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