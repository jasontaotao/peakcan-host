using System.Windows;
using System.Windows.Controls;
using PeakCan.Host.App.ViewModels.Uds;
using PeakCan.Host.Core.Analysis.Uds;

namespace PeakCan.Host.App.Views;

/// <summary>
/// 日志分析页签：业务逻辑全部在 <c>UdsLogAnalysisViewModel</c>，
/// code-behind 只接一件事——「定位到某条事务」时把那一行滚进可视区。
/// <para>
/// <b>为什么这件事不能放在 VM：</b>改 <c>SelectedTransaction</c> 只换右栏内容，
/// DataGrid 不会自己滚到新选中的行；虚拟化表格里目标行往往在视口之外，
/// 用户看到的是「点了没反应」。滚动是纯视图行为。
/// </para>
/// </summary>
public partial class UdsLogAnalysisView : UserControl
{
    private UdsLogAnalysisViewModel? _viewModel;

    public UdsLogAnalysisView()
    {
        InitializeComponent();
        // 页签切换会卸载/重载本控件：两个时机都重新挂，避免重载后定位失效。
        DataContextChanged += (_, e) => Attach(e.NewValue as UdsLogAnalysisViewModel);
        Loaded += (_, _) => Attach(DataContext as UdsLogAnalysisViewModel);
        // 关窗时必须解绑：VM 是 DI 单例，视图随窗口销毁；不解绑的话事件
        // 会一直持有已关闭窗口的视图（及其 DataGrid），每开关一次累积一个，
        // 之后每次分析/筛选都要让所有残留视图各走一遍集合变更。
        Unloaded += (_, _) => Attach(null);
    }

    private void Attach(UdsLogAnalysisViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel)) return;

        if (_viewModel is not null)
            _viewModel.LocateRequested -= RevealTransaction;

        _viewModel = viewModel;

        if (_viewModel is not null)
            _viewModel.LocateRequested += RevealTransaction;
    }

    private void RevealTransaction(UdsOfflineTransaction transaction)
    {
        // SelectedItem 已由 VM 设好，这里只补「滚到它」。
        TransactionGrid.ScrollIntoView(transaction);
        TransactionGrid.UpdateLayout();
    }
}
