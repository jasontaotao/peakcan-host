using System.Windows.Controls;

namespace PeakCan.Host.App.Views;

/// <summary>
/// Code-behind for the Nodes tab view (Task 18). The control is a thin
/// shell around the node list / message &amp; rule tables / activity log
/// defined in <c>NodesView.xaml</c>; all behaviour lives in
/// <see cref="ViewModels.Nodes.NodeSetupViewModel"/>, so this class is
/// intentionally empty beyond <c>InitializeComponent</c> (SendView 同款).
/// </summary>
public partial class NodesView : UserControl
{
    public NodesView()
    {
        InitializeComponent();
    }
}
