using System.Windows;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App.Windows;

public partial class HilStudioWindow : Window
{
    public HilStudioWindow(HilStudioViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
