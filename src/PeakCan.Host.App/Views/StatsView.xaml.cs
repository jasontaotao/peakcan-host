using System.Windows.Controls;

namespace PeakCan.Host.App.Views;

/// <summary>
/// Code-behind for the Stats tab view. The control hosts an
/// <c>OxyPlot.Wpf.PlotView</c> bound to <see cref="ViewModels.StatsViewModel.PlotModel"/>;
/// all chart data and behaviour live in the view-model, so the
/// code-behind is intentionally empty (XAML's <c>InitializeComponent</c>
/// only).
/// </summary>
public partial class StatsView : UserControl
{
    public StatsView()
    {
        InitializeComponent();
    }
}
