using System.Windows.Controls;

namespace PeakCan.Host.App.Views;

/// <summary>
/// Code-behind for the Trace tab view. The control is a thin shell
/// around a virtualized <c>DataGrid</c> bound to
/// <see cref="ViewModels.TraceViewModel.Entries"/>; the view-model
/// owns all behaviour, so the code-behind is intentionally empty
/// (XAML's <c>InitializeComponent</c> only).
/// </summary>
public partial class TraceView : UserControl
{
    public TraceView()
    {
        InitializeComponent();
    }
}
