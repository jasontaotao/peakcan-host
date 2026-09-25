using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using A2lEditor.Core;
using A2lEditor.Core.Layout;
using PeakCan.Host.App.Services.Trace;
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

    /// <summary>
    /// T11（T9 选择器接线）：打开 XCP 对象选择对话框（模态 ShowDialog，T9 L4），
    /// OK 后把 ConfirmedRows 逐行回填进卡片关注集。traceSession 经组合根静态
    /// 服务口解析（ITraceSessionService singleton，D2 关注集持久化归属）；
    /// DI 未就绪（设计时）时为 null → 选择器退化为纯选择模式。
    /// </summary>
    private void OnPickObjects(object sender, RoutedEventArgs e)
    {
        if (DataContext is not XcpViewModel vm)
            return;

        var loaded = vm.Connection.LoadedResult;
        if (loaded is null)
            return; // 未加载态按钮已由 XAML DataTrigger 禁用；此处兜底。

        var pickerVm = new XcpObjectPickerViewModel(loaded.Contracts, ResolveTraceSession());
        var dialog = new XcpObjectPickerWindow(pickerVm) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true)
            return; // 取消：关注集不动。

        ApplyConfirmedRows(vm.Cards, loaded.Contracts, dialog.ConfirmedRows);
    }

    private static ITraceSessionService? ResolveTraceSession()
    {
        // App.xaml.cs 静态 IServiceProvider（注释明示的 ad-hoc 解析口）：
        // XCP tab 视图层无构造注入通道（T11 最小 diff），经此取
        // ITraceSessionService singleton；null 安全（App.Services 未初始化时）。
        var services = App.Services;
        return services?.GetService(typeof(ITraceSessionService)) as ITraceSessionService;
    }

    /// <summary>
    /// T9 评审 M-1：ConfirmedRows → 卡片回填。contract 查找必须扫
    /// <see cref="ContractSet.All"/> 按 (name, category) 对查——ContractSet 的
    /// 按名索引/TryGet 在同名异类（Rpm/MEASUREMENT + Rpm/CHARACTERISTIC）时
    /// 只能返回其一，禁止使用。同名异类碰撞取舍：卡片 AddWatch 按名去重，
    /// 同名第二行被拒绝（单卡只挂一个合同）；卡片键升级为 (name, category)
    /// 前保持此语义（spec 已知限制清单有记录）。
    /// </summary>
    internal static void ApplyConfirmedRows(
        XcpCardPanelViewModel cards,
        ContractSet contracts,
        IReadOnlyList<XcpWatchRow> rows)
    {
        foreach (var row in rows)
        {
            ValueContract? contract = null;
            foreach (var candidate in contracts.All)
            {
                if (candidate.ObjectName == row.Name && CategoryTextOf(candidate.Category) == row.Category)
                {
                    contract = candidate;
                    break;
                }
            }

            if (contract is null)
                continue; // A2L 换版后关注集残留行：声明面无此对 → 跳过不炸。

            cards.AddWatch(row.Name, row.Category, contract);
        }
    }

    /// <summary>类别枚举 → 选择器/XcpWatchRow 使用的 A2L 关键字文本（与 picker 分组同表）。</summary>
    private static string CategoryTextOf(A2lObjectCategory category) => category switch
    {
        A2lObjectCategory.Measurement => "MEASUREMENT",
        A2lObjectCategory.Characteristic => "CHARACTERISTIC",
        A2lObjectCategory.AxisPts => "AXIS_PTS",
        _ => category.ToString(),
    };
}
