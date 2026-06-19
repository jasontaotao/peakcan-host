using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using OxyPlot;
using OxyPlot.Axes;
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
    /// Build the VM and pre-populate the empty rolling-window series so
    /// the chart can render an empty axis range on first render.
    /// </summary>
    public StatsViewModel()
    {
        // Pre-fill the rolling windows with zeros so the chart's X axis
        // renders a stable range from t=0 to t=MaxPoints even before
        // the first 1-second timer tick arrives. OxyPlot binds the
        // series to the source ObservableCollection via a DataPoint
        // wrapper; using double values is the simplest contract.
        for (var i = 0; i < MaxPoints; i++)
        {
            FpsSeries.Add(0.0);
            LoadSeries.Add(0.0);
        }

        PlotModel = new PlotModel
        {
            Title = "Bus statistics (1 Hz, 60-sample rolling window)",
            TitleFontSize = 12,
            PlotAreaBorderColor = OxyColor.FromRgb(0xCC, 0xCC, 0xCC),
        };

        // X axis: 0..MaxPoints sample index. Floating-point range lets
        // OxyPlot render the rolling-window without integer-label
        // rounding noise.
        PlotModel.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Bottom,
            Minimum = 0,
            Maximum = MaxPoints,
            MajorStep = 10,
            Title = "Sample (1 Hz)",
        });
        // Y axis: 0..100 to fit bus-load percent (0..100) and FPS
        // (0..8000 classic 1 Mbps). FPS saturates the chart but the
        // operator is typically looking for trend, not absolute scale.
        // A future task can switch to a second Y axis if needed.
        PlotModel.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Left,
            Minimum = 0,
            Maximum = 100,
            MajorStep = 25,
            Title = "FPS / Load %",
        });

        var fpsLine = new LineSeries
        {
            Title = "Frames / sec",
            Color = OxyColor.FromRgb(0x1F, 0x77, 0xB4),  // Tableau blue
            StrokeThickness = 1.5,
            ItemsSource = FpsSeries,
            // OxyPlot's LineSeries consumes INotifyCollectionChanged on
            // its ItemsSource; adding to / clearing FpsSeries on the UI
            // thread triggers a chart refresh.
        };
        var loadLine = new LineSeries
        {
            Title = "Bus load %",
            Color = OxyColor.FromRgb(0xD6, 0x27, 0x28),  // Tableau red
            StrokeThickness = 1.5,
            ItemsSource = LoadSeries,
        };
        PlotModel.Series.Add(fpsLine);
        PlotModel.Series.Add(loadLine);
    }

    /// <summary>
    /// Append a new bus-statistics sample to the rolling windows and
    /// refresh the totals. Marshals to the WPF UI thread when a
    /// dispatcher is available (production path); runs inline in test
    /// context (no <c>Application</c>).
    /// </summary>
    public void Push(BusStatistics s)
    {
        // Task 19: detect the "leaked Application on a different
        // dispatcher" test-context case (calling thread's dispatcher
        // differs from Application.Current.Dispatcher) and fall back
        // to inline. Without this guard, SignalViewModelTests etc.
        // fail under xunit + XPlat coverage because a previous
        // STA test leaked an Application singleton whose dispatcher
        // sits on an exited STA thread.
        var appDispatcher = System.Windows.Application.Current?.Dispatcher;
        var callingDispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        if (appDispatcher is not null && appDispatcher == callingDispatcher && !callingDispatcher.CheckAccess())
        {
            // Production: 1 Hz timer thread → hop to UI thread.
            // Fire-and-forget via InvokeAsync so the timer thread is
            // not blocked by UI work.
            appDispatcher.InvokeAsync(() => Apply(s));
            return;
        }
        Apply(s);
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
    }
}
