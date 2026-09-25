using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PeakCan.Host.App.ViewModels.Xcp;

namespace PeakCan.Host.App.Views.Xcp;

/// <summary>
/// XCP 主 tab 视图壳（S3-T8，spec D1）。DataContext 是 singleton
/// <see cref="XcpViewModel"/>（AppShell MainTabs TabSpec 工厂注入）。
/// <para>
/// 本类只承载两件视图层义务，业务逻辑全部在 VM 层：
/// <list type="bullet">
/// <item>20 Hz DispatcherTimer 驱动卡片面板 <see cref="XcpCardPanelViewModel.Flush"/>
///（批量 drain + 全卡 RefreshStaleness，spec D3/T5 定案：timer 不进 VM）。
/// Loaded 启动 / Unloaded 停止，随 tab 惰性创建生命周期。</item>
/// <item>A2L 文件浏览对话框（连接面板注释的 IFileDialogService 同款入口）：
/// 仅把选中路径写回 <see cref="XcpConnectionPanelViewModel.A2lPath"/>，加载动作仍由
/// LoadA2LCommand 执行（可测性：对话框不进 VM）。</item>
/// </list>
/// </para>
/// </summary>
public partial class XcpView : UserControl
{
    /// <summary>20 Hz 刷新周期（50 ms）——sink 批量 flush 的 UI 节拍。</summary>
    private const int FlushIntervalMilliseconds = 50;

    private DispatcherTimer? _flushTimer;

    public XcpView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_flushTimer is not null)
            return;

        // 业务逻辑：20 Hz 批量 flush（spec §1 UI 节流）——timer 属视图层，
        // VM 不持 Dispatcher（T5 评审定案）。DataContext 在 tab 工厂处已就位。
        _flushTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(FlushIntervalMilliseconds),
        };
        _flushTimer.Tick += (_, _) => (DataContext as XcpViewModel)?.Cards.Flush();
        _flushTimer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // tab 切走即停拍（singleton VM 状态保留，切回 Loaded 重启）。
        _flushTimer?.Stop();
        _flushTimer = null;
    }

    private void OnBrowseA2l(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 A2L 文件",
            Filter = "A2L 文件 (*.a2l)|*.a2l|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog() == true && DataContext is XcpViewModel vm)
            vm.Connection.A2lPath = dialog.FileName;
    }
}

