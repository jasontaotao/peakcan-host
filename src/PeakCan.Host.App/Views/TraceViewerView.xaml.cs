using System.Windows;
using System.Windows.Controls;
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

    // DELETED (v3.13.0 PATCH F3): OnLoadDbcClick. The Trace Viewer
    // toolbar "Load DBC…" button (XAML line 31) was removed because
    // LoadedDbcPath was never bound in TraceViewerView.xaml — the
    // toolbar click had no UI feedback. The Trace Viewer still reads
    // _dbcService.Current for decoding; DbcView tab is now the single
    // entry point for DBC loading.

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

    /// <summary>
    /// v3.14.3 PATCH: checkbox Click handler for the opt-in column.
    /// Mirrors the v1.2.7 SignalView pattern — WPF's
    /// DataGridCheckBoxColumn edit lifecycle is unreliable on .NET 10
    /// when the parent grid is IsReadOnly=True. The Click event fires
    /// regardless of edit-mode state, so it's the primary path for
    /// chart-plot opt-in.
    /// <para>
    /// Reads the CheckBox.IsChecked UI value (just toggled by the
    /// click) and forwards the explicit opt-in intent to the VM via
    /// <c>SetPlotOptIn</c>. The TwoWay binding on IsPlotted updates
    /// the row's INPC field as a side effect, but we don't depend on
    /// it — the UI-side IsChecked is the source of truth at click time.
    /// </para>
    /// </summary>
    /// <summary>
    /// v3.16.0 MINOR: open the DBC tree picker dialog. The user
    /// selects one or more signals; we call
    /// <c>TraceViewerViewModel.AddToWatch</c> for each (cross-source).
    /// </summary>
    private void OnAddToWatchClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TraceViewerViewModel vm) return;
        var doc = vm.GetDbcForPicker();
        if (doc is null) return;  // VM already surfaced a status message
        var pickerVm = new DbcTreePickerViewModel(doc);
        var dialog = new DbcTreePickerWindow(pickerVm) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        foreach (var (canId, signalName) in dialog.SelectedSignals)
            vm.AddToWatch(canId, signalName, "");
    }

    private void OnPlotCheckboxClick(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { IsChecked: bool isChecked } cb
            && cb.DataContext is TraceSignalRow row
            && DataContext is TraceViewerViewModel vm)
        {
            // Ignore stale reentry if row was already at this state
            // (e.g. virtualizing recycle of an off-screen row).
            if (row.IsPlotted == isChecked) return;
            vm.SetPlotOptIn(row, isChecked);
        }
    }
}
