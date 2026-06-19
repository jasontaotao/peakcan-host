using System.Windows.Controls;

namespace PeakCan.Host.App.Views;

/// <summary>
/// Code-behind for the Send tab view. The control is a thin shell around
/// the manual-send form defined in <c>SendView.xaml</c>; all behaviour
/// lives in <see cref="ViewModels.SendViewModel"/>, so this class is
/// intentionally empty beyond <c>InitializeComponent</c>.
/// </summary>
public partial class SendView : UserControl
{
    public SendView()
    {
        InitializeComponent();
    }
}
