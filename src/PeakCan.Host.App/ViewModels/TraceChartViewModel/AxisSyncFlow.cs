using OxyPlot.Axes;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class TraceChartViewModel
{
    // Flow E: AxisSync (v3.3.2 PATCH + earlier).
    // Methods moved verbatim from TraceChartViewModel.cs.
    //
    // Cross-flow callers (stay as plain calls via partial-class visibility):
    //   - SyncXAxis -> Series (state, main)
    //   - SyncYAxes -> Series (state, main)

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
}