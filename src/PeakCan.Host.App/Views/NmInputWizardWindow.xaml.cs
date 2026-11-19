using System.Windows;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App.Views;

public partial class NmInputWizardWindow : Window
{
    public NmInputWizardWindow(NmInputWizardViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.RequestClose += OnRequestClose;
    }

    /// <summary>保存成功后自动关闭；本窗口只以 ShowDialog 打开，DialogResult 合法。</summary>
    private void OnRequestClose(object? sender, EventArgs e) => DialogResult = true;

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();
}
