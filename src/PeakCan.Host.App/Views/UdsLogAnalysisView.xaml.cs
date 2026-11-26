using System.Windows.Controls;

namespace PeakCan.Host.App.Views;

/// <summary>
/// 日志分析页签的纯视图：所有逻辑在 <c>UdsLogAnalysisViewModel</c>，
/// code-behind 只负责加载 XAML。
/// </summary>
public partial class UdsLogAnalysisView : UserControl
{
    public UdsLogAnalysisView()
    {
        InitializeComponent();
    }
}
