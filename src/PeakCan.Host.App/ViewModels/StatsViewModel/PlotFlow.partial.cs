using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Legends;
using OxyPlot.Series;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// W36 god-class refactor (21st overall): OxyPlot PlotModel + 2 LineSeries
/// setup extracted from main. Sister of W34 DbcSendViewModel/ subdirectory
/// pattern (D1: 2 partials; D4 upgrade: ctor-as-orchestrator moves to
/// PlotFlow because the ctor body IS the chart-construction logic,
/// not just DI wiring).
/// <para>
/// Per hard-boundary v1.2.7: LineSeries.ItemsSource binding to
/// ObservableCollection is broken on .NET 10 / OxyPlot 2.2.0. The
/// explicit Points rebuild happens in SamplingFlow.Apply.
/// </para>
/// </summary>
public sealed partial class StatsViewModel
{
    /// <summary>
    /// Build the VM and pre-populate the empty rolling-window series so
    /// the chart can render an empty axis range on first render.
    /// </summary>
    public StatsViewModel()
    {
        // Pre-fill the rolling windows with zeros so the chart's X axis
        // renders a stable range from t=0 to t=MaxPoints even before
        // the first 1-second timer tick arrives.
        //
        // v1.2.7: we maintain TWO parallel representations of the same
        // rolling window — FpsSeries / LoadSeries (ObservableCollection<double>,
        // kept for backward-compat with existing tests and any external
        // binding) and _fpsLine.Points / _loadLine.Points
        // (OxyPlot DataPoint list, the source of truth for what the
        // chart actually renders on .NET 10). The LineSeries no longer
        // has an ItemsSource binding.
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

        _fpsLine = new LineSeries
        {
            Title = "Frames / sec",
            Color = OxyColor.FromRgb(0x1F, 0x77, 0xB4),  // Tableau blue
            StrokeThickness = 1.5,
        };
        _loadLine = new LineSeries
        {
            Title = "Bus load %",
            Color = OxyColor.FromRgb(0xD6, 0x27, 0x28),  // Tableau red
            StrokeThickness = 1.5,
        };
        // Pre-fill the LineSeries Points with zeros so the X axis
        // range is stable from t=0 to t=MaxPoints on first render.
        for (var i = 0; i < MaxPoints; i++)
        {
            _fpsLine.Points.Add(new DataPoint(i, 0.0));
            _loadLine.Points.Add(new DataPoint(i, 0.0));
        }
        PlotModel.Series.Add(_fpsLine);
        PlotModel.Series.Add(_loadLine);

        // v1.2.10: OxyPlot 2.2.0 doesn't render series labels unless a
        // Legend is registered on the PlotModel. Without this the FPS /
        // Bus-load series names show up only as data, no legend.
        PlotModel.Legends.Add(new Legend
        {
            LegendPlacement = LegendPlacement.Outside,
            LegendPosition = LegendPosition.RightTop,
            LegendBackground = OxyColor.FromAColor(32, OxyColor.FromRgb(0xFF, 0xFF, 0xFF)),
            LegendBorder = OxyColor.FromRgb(0xCC, 0xCC, 0xCC),
            LegendBorderThickness = 1,
        });
    }
}