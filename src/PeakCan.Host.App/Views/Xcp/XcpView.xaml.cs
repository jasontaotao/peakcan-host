using System.Collections.ObjectModel;
using System.Collections.Specialized;
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

    /// <summary>状态区订阅的集合（Unloaded 时按同一实例退订，避免重复挂钩）。</summary>
    private ObservableCollection<string>? _connectionStatus;
    private ObservableCollection<string>? _acquisitionStatus;

    public XcpView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        // DataContext 可能晚于 Loaded 才就位（设计时 / tab 工厂时序）：两个时机都尝试挂一次。
        DataContextChanged += (_, _) => SubscribeToStatusLines();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 订阅必须放在下面"timer 非空就早退"之前：那个早退是给 Loaded 二次触发
        // 准备的，订阅跟着被跳过的话自动滚动会永久静默失效且没有任何提示。
        SubscribeToStatusLines();

        if (_flushTimer is not null)
            return;

        // 业务逻辑：20 Hz 批量 flush（spec §1 UI 节流）——timer 属视图层，
        // VM 不持 Dispatcher（T5 评审定案）。DataContext 在 tab 工厂处已就位。
        _flushTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(FlushIntervalMilliseconds),
        };
        _flushTimer.Tick += (_, _) =>
        {
            var vm = DataContext as XcpViewModel;
            vm?.Cards.Flush();
            vm?.Record.RefreshState(); // S4-T5：记录状态行同拍刷新。
        };
        _flushTimer.Start();
        // S6-T5：diff 面板"定位对象卡片"承接——滚动卡片格到目标对象（视图层义务）。
        if (DataContext is XcpViewModel { Diff: { } diff })
            diff.CardLocateRequested += OnDiffCardLocateRequested;
        // P0-1：tab 晚于 A2L 加载创建时补装 MAP 清单（幂等）。
        (DataContext as XcpViewModel)?.Map?.RefreshMaps();
    }

    /// <summary>
    /// 订阅两块状态区（幂等）：状态区限高后新行会落在折线以下，而结论行
    ///（"采集已启动" / "对账拒绝启动"）恰恰补在最后，集合一变就滚到底，
    /// 保证最该被看见的那一行始终在视野里。DataContext 换实例时先退旧再挂新。
    /// </summary>
    private void SubscribeToStatusLines()
    {
        if (DataContext is not XcpViewModel vm)
            return;
        if (ReferenceEquals(_connectionStatus, vm.Connection.StatusLines))
            return;

        UnsubscribeFromStatusLines();
        _connectionStatus = vm.Connection.StatusLines;
        _acquisitionStatus = vm.Acquisition.StatusLines;
        _connectionStatus.CollectionChanged += OnStatusLinesChanged;
        _acquisitionStatus.CollectionChanged += OnStatusLinesChanged;
        // A2L 可能先于本 tab 创建就已加载：挂载时集合里已有内容，先滚一次。
        ScrollStatusToEnd();
    }

    private void UnsubscribeFromStatusLines()
    {
        if (_connectionStatus is not null)
            _connectionStatus.CollectionChanged -= OnStatusLinesChanged;
        if (_acquisitionStatus is not null)
            _acquisitionStatus.CollectionChanged -= OnStatusLinesChanged;
        _connectionStatus = null;
        _acquisitionStatus = null;
    }

    private void OnStatusLinesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        ScrollStatusToEnd();

    /// <summary>
    /// 展开时补滚到底：折叠状态下内容没有测量过，ScrollToEnd 是空操作；
    /// 用户点开时最新的行才落进视野。
    /// </summary>
    private void OnStatusExpanderExpanded(object sender, RoutedEventArgs e) => ScrollStatusToEnd();

    /// <summary>
    /// 滚到两块状态明细的末尾。集合变化时布局尚未重算，直接 ScrollToEnd 会按旧范围夹取
    /// ——排到 Loaded 优先级（低于 Render 的布局趟）再滚，此时新行已测量完毕。
    /// </summary>
    private void ScrollStatusToEnd() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            ConnectionStatusScroll?.ScrollToEnd();
            AcquisitionStatusScroll?.ScrollToEnd();
        }));

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // tab 切走即停拍（singleton VM 状态保留，切回 Loaded 重启）。
        _flushTimer?.Stop();
        _flushTimer = null;
        if (DataContext is XcpViewModel { Diff: { } diff })
            diff.CardLocateRequested -= OnDiffCardLocateRequested;

        UnsubscribeFromStatusLines();
    }

    /// <summary>S6-T5：滚动卡片格到 diff 选中的对象（无该卡片时状态不变化）。</summary>
    private void OnDiffCardLocateRequested(string objectName)
    {
        var vm = DataContext as XcpViewModel;
        if (vm is null || CardsItemsControl is null)
            return;
        var generator = CardsItemsControl.ItemContainerGenerator;
        for (var i = 0; i < vm.Cards.Cards.Count; i++)
        {
            if (!string.Equals(vm.Cards.Cards[i].Name, objectName, System.StringComparison.Ordinal))
                continue;
            if (generator.ContainerFromIndex(i) is System.Windows.FrameworkElement container)
                container.BringIntoView();
            return;
        }
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
    /// S4-T5：记录目录浏览（spec D5 可改选）。仅把选中目录写回 VM，
    /// 对话框不进 VM（可测性，OnBrowseA2l 同款口径）。
    /// </summary>
    private void OnBrowseRecordDirectory(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择记录目录" };
        if (dialog.ShowDialog() == true && DataContext is XcpViewModel vm)
            vm.Record.RecordDirectory = dialog.FolderName;
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
