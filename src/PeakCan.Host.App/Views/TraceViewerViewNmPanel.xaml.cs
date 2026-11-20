using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App.Views;

public partial class TraceViewerViewNmPanel : UserControl
{
    public TraceViewerViewNmPanel() => InitializeComponent();

    /// <summary>
    /// spec §9.1 / AC6：事件行单击即 seek。只处理真正落在 DataGridRow 上的点击，
    /// 避免点表头/空白处把上一行再 seek 一次。
    /// </summary>
    private void OnEventRowClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not TraceViewerViewModel vm || sender is not DataGrid grid) return;
        if (e.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement(grid, source) is not DataGridRow row) return;
        if (row.Item is NmEventRowVm nmRow)
            vm.SeekToNmEventCommand.Execute(nmRow);
    }

    /// <summary>保留双击 seek（原有行为）。</summary>
    private void OnEventRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is TraceViewerViewModel vm && sender is DataGrid grid
            && grid.SelectedItem is NmEventRowVm row)
        {
            vm.SeekToNmEventCommand.Execute(row);
        }
    }
}
