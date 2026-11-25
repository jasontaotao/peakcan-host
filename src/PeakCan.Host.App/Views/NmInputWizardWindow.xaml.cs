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

        // 文件对话框留在视图层：VM 只暴露可注入的委托，测试不会弹窗。
        vm.SaveFilePicker = suggestedName =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出 NM 配置",
                Filter = "NM profile (*.nmprofile.json)|*.nmprofile.json|JSON (*.json)|*.json",
                FileName = suggestedName,
                AddExtension = true,
                DefaultExt = ".json",
            };
            return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        };
        vm.OpenFilePicker = () =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "导入 NM 配置",
                Filter = "NM profile (*.nmprofile.json)|*.nmprofile.json|JSON (*.json)|*.json",
                CheckFileExists = true,
            };
            return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        };
        vm.ReasonDictionaryPicker = () =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择原因字典",
                Filter = "NM 原因字典 (*.nmreasons.json)|*.nmreasons.json|JSON (*.json)|*.json",
                CheckFileExists = true,
            };
            return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        };
    }

    /// <summary>保存成功后自动关闭；本窗口只以 ShowDialog 打开，DialogResult 合法。</summary>
    private void OnRequestClose(object? sender, EventArgs e) => DialogResult = true;

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();
}
