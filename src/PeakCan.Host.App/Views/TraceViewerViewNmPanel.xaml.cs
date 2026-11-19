using System.Windows.Controls;
using System.Windows.Input;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App.Views;

public partial class TraceViewerViewNmPanel : UserControl
{
    public TraceViewerViewNmPanel() => InitializeComponent();

    private void OnEventRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is TraceViewerViewModel vm && sender is DataGrid grid
            && grid.SelectedItem is NmEventRowVm row)
        {
            vm.SeekToNmEventCommand.Execute(row);
        }
    }
}
