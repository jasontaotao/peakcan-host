using System.Windows;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App.Windows;

/// <summary>
/// 独立非模态 ECU 脚本 JSON 编辑器窗口.
/// ctor 收 VM 设 DataContext; 无业务逻辑.
/// </summary>
public partial class EcuScriptEditorWindow : Window
{
    public EcuScriptEditorWindow(EcuScriptEditorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
