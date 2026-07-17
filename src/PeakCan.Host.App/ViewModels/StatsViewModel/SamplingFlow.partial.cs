using OxyPlot;
using PeakCan.Host.App.Helpers;
using PeakCan.Host.Infrastructure.Statistics;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// W36 god-class refactor (21st overall): Push dispatcher hop + Apply
/// rolling-window maintenance + LineSeries.Points rebuild extracted from
/// main. Sister of W34 DbcSendViewModel/SendFlow.partial.cs pattern.
/// <para>
/// Per hard-boundary v1.2.7: LineSeries.Points MUST be explicitly rebuilt
/// (not ItemsSource binding) due to OxyPlot 2.2.0 .NET 10 broken binding.
/// Per v1.2.9: rebuilt points use index 0..MaxPoints-1 as X (mirrors the
/// implicit ObservableCollection index-as-X semantics).
/// </para>
/// </summary>
public sealed partial class StatsViewModel
{
    /// <summary>
    /// Append a new bus-statistics sample to the rolling windows and
    /// refresh the totals. Marshals to the WPF UI thread when a
    /// dispatcher is available (production path); runs inline in test
    /// context (no <c>Application</c>).
    /// </summary>
    public void Push(BusStatistics s)
    {
        // The previous Task 19 guard (`appDispatcher == callingDispatcher`)
        // was inverted and silently skipped the hop in production; the
        // chokepoint is now DispatcherExtensions.RunOnUi. Fire-and-forget
        // because the 1 Hz timer thread must not block on UI work.
        ((Action)(() => Apply(s))).RunOnUiPost();
    }

    /// <summary>
    /// Apply the snapshot inline. Always called on the UI thread (via
    /// the dispatcher hop above, or directly in test context). Maintains
    /// the rolling window at <see cref="MaxPoints"/> samples and
    /// refreshes the bound totals.
    /// </summary>
    private void Apply(BusStatistics s)
    {
        TotalFrames = s.TotalFrames;
        ErrorFrames = s.ErrorFrames;
        FpsSeries.Add(s.FramesPerSecond);
        LoadSeries.Add(s.BusLoadPercent);
        while (FpsSeries.Count > MaxPoints) FpsSeries.RemoveAt(0);
        while (LoadSeries.Count > MaxPoints) LoadSeries.RemoveAt(0);
        // v1.2.9: rebuild the LineSeries Points from the FpsSeries /
        // LoadSeries ObservableCollection. Pre-1.2.9 every new sample
        // was added at X=MaxPoints-1, which collapsed all points onto
        // a single vertical line at X=59 and made the chart look like
        // a one-pixel stick. Rebuilt points have correct X indices
        // 0..MaxPoints-1 that match the collection ordering, mirroring
        // the original ObservableCollection's index-as-X semantics
        // (OxyPlot 2.2.0's ItemsSource path on .NET 10 used the
        // collection index as X implicitly; the explicit Points
        // approach requires us to encode the X position ourselves).
        // Cost: O(N) per sample with N=MaxPoints=60 — negligible.
        _fpsLine.Points.Clear();
        for (int i = 0; i < FpsSeries.Count; i++)
        {
            _fpsLine.Points.Add(new DataPoint(i, FpsSeries[i]));
        }
        _loadLine.Points.Clear();
        for (int i = 0; i < LoadSeries.Count; i++)
        {
            _loadLine.Points.Add(new DataPoint(i, LoadSeries[i]));
        }
        // v1.2.10: OxyPlot 2.2.0 WPF doesn't auto-invalidate on LineSeries.Points
        // mutation; without explicit InvalidatePlot the chart stays frozen at the
        // constructor pre-fill. updateData:true forces OxyPlot to re-process Points.
        InvalidatePlotCallCount++;
        PlotModel.InvalidatePlot(updateData: true);
    }
}