using Microsoft.Win32;
using System.Windows.Controls;
using System.Windows;
using PeakCan.Host.App.ViewModels.Xcp;

namespace PeakCan.Host.App.Views.Xcp;

/// <summary>
/// S6-T5 参数集 diff 页视图（spec D5）。VM 持状态与 diff 行集；
/// 本 code-behind 只持文件浏览对话框与 XcpView 卡片定位承接。
/// 只读——本页无任何写路径（S1 钉，应用走 S5 既有 apply）。
/// </summary>
public partial class XcpDiffView : UserControl
{
    public XcpDiffView()
    {
        InitializeComponent();
    }

    private void OnBrowseBaseline(object sender, RoutedEventArgs e) =>
        Browse(XcpDiffViewModelPath.Baseline);

    private void OnBrowseTarget(object sender, RoutedEventArgs e) =>
        Browse(XcpDiffViewModelPath.Target);

    private void Browse(XcpDiffViewModelPath which)
    {
        if (DataContext is not XcpDiffPanelViewModel vm)
            return;
        var dialog = new OpenFileDialog
        {
            Filter = "参数集 JSON (*.json)|*.json|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true)
            return;
        if (which == XcpDiffViewModelPath.Baseline)
            vm.BaselinePath = dialog.FileName;
        else
            vm.TargetPath = dialog.FileName;
    }

    private enum XcpDiffViewModelPath
    {
        Baseline,
        Target,
    }
}
