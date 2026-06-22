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
}
