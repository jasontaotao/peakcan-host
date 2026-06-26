using System.Windows;
using System.Windows.Controls;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App.Views;

/// <summary>
/// Code-behind for the Signal tab view. The control hosts a virtualized
/// <c>DataGrid</c> bound to <see cref="ViewModels.SignalViewModel.Latest"/>
/// and an OxyPlot chart bound to
/// <see cref="ViewModels.SignalViewModel.ChartModel"/>.
/// <para>
/// The <see cref="OnCellEditEnding"/> handler wires the "Plot" checkbox
/// column to <see cref="ViewModels.SignalViewModel.OnSignalSelectionChanged"/>
/// so the chart VM can add/remove signal series.
/// </para>
/// </summary>
public partial class SignalView : UserControl
{
    public SignalView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Wire the "Plot" checkbox change to the VM. The WPF binding
    /// updates <see cref="SignalEntry.IsSelected"/> before this
    /// handler fires (thanks to <c>UpdateSourceTrigger=PropertyChanged</c>).
    /// </summary>
    private void OnCellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.Column is DataGridCheckBoxColumn
            && e.EditAction == DataGridEditAction.Commit
            && e.Row.DataContext is SignalEntry entry
            && DataContext is SignalViewModel vm)
        {
            vm.OnSignalSelectionChanged(entry.Message, entry.Signal, entry.IsSelected);
        }
    }

    /// <summary>
    /// v1.2.7: <see cref="OnCellEditEnding"/> does not fire reliably
    /// for checkbox clicks when the DataGrid is read-only at the
    /// parent level (a known WPF gotcha that the .NET 10 build
    /// exposed). Replaced the <c>DataGridCheckBoxColumn</c> with a
    /// <c>DataGridTemplateColumn</c> + explicit <c>CheckBox</c>; the
    /// <c>Click</c> event fires regardless of DataGrid edit-mode
    /// state, so this handler is the primary path for chart-plot
    /// selection. <see cref="OnCellEditEnding"/> is kept as a
    /// belt-and-braces fallback for any future scenario where
    /// WPF's edit lifecycle does fire (e.g. keyboard activation).
    /// </summary>
    private void OnPlotCheckboxClick(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: SignalEntry entry }
            && DataContext is SignalViewModel vm)
        {
            vm.OnSignalSelectionChanged(entry.Message, entry.Signal, entry.IsSelected);
        }
    }
}
