using System.Windows.Controls;

namespace PeakCan.Host.App.Views;

/// <summary>
/// Code-behind for the Signal tab view. The control is a thin shell
/// around a virtualized <c>DataGrid</c> bound to
/// <see cref="ViewModels.SignalViewModel.Latest"/>; the view-model owns
/// all behaviour, so the code-behind is intentionally empty (XAML's
/// <c>InitializeComponent</c> only).
/// </summary>
public partial class SignalView : UserControl
{
    public SignalView()
    {
        InitializeComponent();
    }
}