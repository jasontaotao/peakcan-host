namespace PeakCan.Host.App.ViewModels;
using ScottPlot;

public sealed partial class TraceChartViewModel
{
    // Flow E: AxisSync (v3.3.2 PATCH + earlier).
    // v3.62.0 MINOR: migrated from OxyPlot LinearAxis → ScottPlot IAxes.SetLimitsX/Y.

    /// <summary>v3.62.0: optional resolver to get the active WpfPlot.Plot by signal key.
    /// Set by parent TraceViewModel. When null, axis sync is a no-op.</summary>
    public Func<string, Plot?>? PlotResolver { get; set; }

    /// <summary>Called by subplot's X-axis when user zooms/pans. Syncs all others.</summary>
    public void SyncXAxis(double minimum, double maximum)
    {
        foreach (var s in Series)
        {
            var plot = PlotResolver?.Invoke(s.SignalKey);
            if (plot is null) continue;
            var xAxis = plot.Axes.Bottom;
            if (xAxis.Min == minimum && xAxis.Max == maximum) continue;
            plot.Axes.SetLimitsX(minimum, maximum);
            s.RefreshCallback?.Invoke();
        }
    }

    /// <summary>
    /// v3.3.2 PATCH: cross-source Y-axis auto-scale coordination.
    /// v3.62.0 MINOR: uses frames directly (YValues populated progressively).
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
                    if (double.IsNaN(y)) continue;
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
                var plot = PlotResolver?.Invoke(s.SignalKey);
                if (plot is null) continue;
                plot.Axes.SetLimitsY(yMin, yMax);
                s.RefreshCallback?.Invoke();
            }
        }
    }
}
