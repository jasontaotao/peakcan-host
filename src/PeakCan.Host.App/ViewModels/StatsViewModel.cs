using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Legends;
using OxyPlot.Series;
using PeakCan.Host.Infrastructure.Statistics;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// Backing view model for the Stats tab (<c>StatsView.xaml</c>).
/// Owns a rolling 60-sample window of frames-per-second and bus-load
/// values, refreshed once per second by <see cref="Services.StatisticsService"/>.
/// <para>
/// <b>Plan deviation:</b> the original plan §17 code blocks reference
/// <c>LiveChartsCore</c> types (CartesianChart, LineSeries&lt;double&gt;,
/// ISeries). <c>LiveChartsCore 2.0.4</c>'s native dependencies
/// (<c>OpenTK</c> + <c>SkiaSharp.Views.WPF</c>) target .NET Framework only
/// — verified during Sprint 0. <c>OxyPlot.Wpf 2.2.0</c> is the replacement
/// (already a project dependency from Task 11 chart prep). The public
/// surface — <see cref="FpsSeries"/>, <see cref="LoadSeries"/>,
/// <see cref="TotalFrames"/>, <see cref="ErrorFrames"/> — is unchanged
/// from plan §17 so the view binding story is identical; only the chart
/// library behind it differs.
/// </para>
/// <para>
/// <b>Concurrency model:</b> <see cref="Push"/> is called from the
/// <see cref="Services.StatisticsService"/> timer thread. The
/// <see cref="FpsSeries"/> / <see cref="LoadSeries"/> mutations MUST
/// marshal to the WPF UI thread (mirror Task 15
/// <see cref="DbcViewModel.OnLoaded"/> + Task 16
/// <see cref="SignalViewModel.ApplyFrame"/>). The <see cref="PlotModel"/>
/// is also a UI-thread construct; the series are added to it at
/// construction time and refreshed via the dispatcher hop in
/// <see cref="Push"/>. In test contexts (no <c>Application</c>) the
/// dispatcher is null and mutations run inline.
/// </para>
/// <para>
/// <b>No <c>IDisposable</c>:</b> DI singleton that lives for the whole
/// app lifetime; both the VM and <see cref="Services.StatisticsService"/>
/// die together at process exit (same rationale as DbcViewModel and
/// SignalViewModel per the Task 15 HIGH-2 review fix).
/// </para>
/// </summary>
public sealed partial class StatsViewModel : ObservableObject
{
    /// <summary>
    /// Maximum number of points retained on the rolling window. At
    /// 1 Hz sampling, 60 = 1 minute of history — enough for at-a-glance
    /// trending, short enough to keep the chart readable.
    /// </summary>
    private const int MaxPoints = 60;

    /// <summary>
    /// Rolling-window of frames-per-second samples. The X axis of the
    /// chart is the sample index (0..59); Y is the value. Mutated on
    /// the WPF UI thread (see <see cref="Push"/>).
    /// </summary>
    public ObservableCollection<double> FpsSeries { get; } = new();

    /// <summary>
    /// Rolling-window of bus-load-percentage samples, parallel to
    /// <see cref="FpsSeries"/>. Mutated on the WPF UI thread.
    /// </summary>
    public ObservableCollection<double> LoadSeries { get; } = new();

    /// <summary>
    /// Total frames observed since the bus was connected. Bound to the
    /// "Total" text in the stats header. Updated on every
    /// <see cref="Push"/> call.
    /// </summary>
    [ObservableProperty]
    private long _totalFrames;

    /// <summary>
    /// Sub-count of <see cref="TotalFrames"/> flagged as error frames.
    /// Bound to the "Errors" text in the stats header.
    /// </summary>
    [ObservableProperty]
    private long _errorFrames;

    /// <summary>
    /// OxyPlot chart model bound to the <c>PlotView</c> in
    /// <c>StatsView.xaml</c>. Two <see cref="LineSeries"/> (FPS in blue,
    /// bus-load % in red) share a single time-based X axis. The series
    /// are constructed once here and their <c>ItemsSource</c> is rebound
    /// to <see cref="FpsSeries"/> / <see cref="LoadSeries"/>; OxyPlot
    /// listens to <see cref="System.Collections.Specialized.INotifyCollectionChanged"/>
    /// on the source collections and refreshes the chart automatically.
    /// </summary>
    public PlotModel PlotModel { get; }

    /// <summary>
    /// v1.2.10: test-only counter tracking how many times Apply called
    /// <see cref="PlotModel.InvalidatePlot"/>. The OxyPlot Updated event
    /// doesn't fire from InvalidatePlot directly (only from
    /// IPlotModel.Update, called by PlotView during render), so this
    /// counter is the deterministic unit-test surface for verifying
    /// Apply triggers an OxyPlot redraw cycle. Mirrors the project's
    /// internal test-hook pattern (FilterRebuildCount, DrainCount).
    /// </summary>
    internal int InvalidatePlotCallCount { get; private set; }

    // v1.2.7: hold direct references to the LineSeries so we can
    // mutate series.Points explicitly. OxyPlot 2.2.0's WPF binding
    // of LineSeries.ItemsSource to ObservableCollection<T> is
    // broken on .NET 10 windows (the LineSeries does not render
    // the source-collection updates, even though the binding
    // path is technically subscribed). SignalChartViewModel uses
    // the working pattern (series.Points.Add) which is why the
    // Signal chart renders fine and only the Stats chart was
    // empty. See docs/release-notes-v1.2.7.md for full diagnosis.
    private readonly LineSeries _fpsLine;
    private readonly LineSeries _loadLine;

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
