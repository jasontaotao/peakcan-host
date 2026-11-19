using System.Windows;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App.Views;

public partial class NmInputWizardWindow : Window
{
    public NmInputWizardWindow(NmInputWizardViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();
}
