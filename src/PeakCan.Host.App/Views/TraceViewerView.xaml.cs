using System.Windows;
using Microsoft.Win32;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App.Views;

public partial class TraceViewerView : Window
{
    public TraceViewerView()
    {
        InitializeComponent();
    }

    public TraceViewerView(TraceViewerViewModel vm) : this()
    {
        DataContext = vm;
    }

    // v3.9.2 PATCH H2: OnAddTraceClick was deleted — v3.9.1 PATCH Bug #2
    // moved the toolbar button to Command="{Binding AddTraceCommand}" so
    // the click handler became dead code (XAML no longer wires
    // Click="OnAddTraceClick"). AddTraceCommand opens the file dialog via
    // CommandParameter="" and surfaces failures via vm.ErrorMessage /
    // vm.StatusMessage.

    private async void OnLoadDbcClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "DBC files|*.dbc;*.DBC|All files|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        if (DataContext is TraceViewerViewModel vm)
        {
            try { await vm.LoadDbcAsync(dlg.FileName); }
            catch (System.Exception ex) { MessageBox.Show(this, ex.Message, "DBC load failed"); }
        }
    }

    // v3.0.2 PATCH Task 2: header buttons inside each subplot DataTemplate.
    // DataContext inside the template is the TraceChartSeries row, so we
    // cast sender.DataContext to TraceChartSeries and the window's
    // DataContext (the VM) to TraceViewerViewModel, then forward to the
    // chart VM's SetFocus / ToggleCollapse methods (both added in Task 1).
    private void OnFocusSubplotClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe
            && fe.DataContext is TraceChartSeries s
            && DataContext is TraceViewerViewModel vm)
        {
            vm.ChartViewModel.SetFocus(s);
        }
    }

    private void OnCollapseSubplotClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe
            && fe.DataContext is TraceChartSeries s
            && DataContext is TraceViewerViewModel vm)
        {
            vm.ChartViewModel.ToggleCollapse(s);
        }
    }

    // v3.0.2 PATCH Task 2: feed ChartAreaHeight (which feeds
    // AdaptiveHeight via RecomputeHeights) from the chart area's actual
    // height. Loaded fires once on first render; SizeChanged fires on
    // window resize and GridSplitter drag.
    private void OnChartScrollLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is TraceViewerViewModel vm && sender is FrameworkElement fe)
        {
            vm.ChartViewModel.ChartAreaHeight = fe.ActualHeight;
        }
    }

    private void OnChartScrollSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DataContext is TraceViewerViewModel vm)
        {
            vm.ChartViewModel.ChartAreaHeight = e.NewSize.Height;
        }
    }
}
