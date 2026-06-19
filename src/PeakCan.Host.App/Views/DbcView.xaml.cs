using System.Windows.Controls;

namespace PeakCan.Host.App.Views;

/// <summary>
/// Code-behind for the DBC tab view. The control is a thin shell around
/// the DataGrid + status strip defined in <c>DbcView.xaml</c>; all
/// behaviour lives in <see cref="ViewModels.DbcViewModel"/>, so this
/// class is intentionally empty beyond <c>InitializeComponent</c>.
/// </summary>
public partial class DbcView : UserControl
{
    public DbcView()
    {
        InitializeComponent();
    }
}