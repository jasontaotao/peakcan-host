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

    // v3.2.0 MINOR: toolbar button renamed "Open .asc…" → "Add trace…" with
    // the same click semantics — calls AddTraceAsync which appends to the
    // session. Single-trace default behavior unchanged from v3.0.
    private async void OnAddTraceClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "ASC files|*.asc;*.ASC|All files|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        if (DataContext is TraceViewerViewModel vm)
        {
            try { await vm.AddTraceAsync(dlg.FileName); }
            catch (System.Exception ex) { MessageBox.Show(this, ex.Message, "Add trace failed"); }
        }
    }

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

    // v3.5.0 MINOR: pop a SaveFileDialog and forward the chosen path to
    // the VM. The VM does not own a WPF dependency; the View handles the
    // file dialog and the post-action MessageBox UX for missing-asc paths.
    private async void OnSaveSessionClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Trace Viewer session|*.tmtrace|All files|*.*",
            DefaultExt = ".tmtrace",
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        if (DataContext is TraceViewerViewModel vm)
        {
            try
            {
                await vm.SaveSessionAsync(dlg.FileName);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Save session failed");
            }
        }
    }

    // v3.5.0 MINOR: pop an OpenFileDialog and forward the chosen path to
    // the VM. The VM returns a list of .asc paths that could not be
    // resolved (e.g. file moved/deleted since the bundle was saved);
    // surface them via MessageBox so the user can decide whether to
    // remap or proceed.
    private async void OnOpenSessionClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Trace Viewer session|*.tmtrace;*.TMTRACE|All files|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        if (DataContext is TraceViewerViewModel vm)
        {
            try
            {
                var missing = await vm.OpenSessionAsync(dlg.FileName);
                if (missing.Count > 0)
                {
                    var msg = "The following recordings could not be located:\n\n"
                              + string.Join("\n", missing)
                              + "\n\nThe session was restored with the remaining sources.";
                    MessageBox.Show(this, msg, "Some sources are missing",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (System.Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Open session failed");
            }
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
