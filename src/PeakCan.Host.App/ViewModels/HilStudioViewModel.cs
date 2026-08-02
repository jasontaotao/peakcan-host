using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels.EcuSimulator;
using PeakCan.Host.App.ViewModels.TestSuiteBuilder;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// HIL Configuration Studio 主 VM。Phase 1 只实现 DBC Browser 栏；
/// Phase 2/3 消费 <see cref="SelectedMessage"/> / <see cref="SelectedSignal"/>。
/// 共享单例 DbcService, 事件永不退订（随进程退出）, OnLoaded 幂等重建。
/// </summary>
public sealed partial class HilStudioViewModel : ObservableObject
{
    private readonly DbcService _svc;
    private readonly IFileDialogService _fileDialog;
    private readonly ILogger<HilStudioViewModel> _logger;
    private readonly List<HilStudioDbcMessageRow> _allMessages = new();

    public ObservableCollection<HilStudioDbcMessageRow> Messages { get; } = new();
    public ObservableCollection<HilStudioDbcMessageRow> FilteredMessages { get; } = new();

    /// <summary>选中消息在信号 grid 显示的行（搜索时只显示匹配信号）。</summary>
    public ObservableCollection<HilStudioDbcSignalRow> VisibleSignals { get; } = new();

    /// <summary>col2 Test Suite Builder 子面板 VM。</summary>
    public TestSuiteBuilderViewModel SuiteBuilder { get; }

    /// <summary>col4 ECU Simulator 子面板 VM。</summary>
    public EcuSimulatorViewModel EcuSimulator { get; }

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _status = "No DBC loaded";
    [ObservableProperty] private string _loadedPath = "";
    [ObservableProperty] private int _totalMessages;
    [ObservableProperty] private int _totalSignals;
    [ObservableProperty] private HilStudioDbcMessageRow? _selectedMessage;
    [ObservableProperty] private HilStudioDbcSignalRow? _selectedSignal;

    public HilStudioViewModel(
        DbcService svc,
        ILogger<HilStudioViewModel> logger,
        IFileDialogService? fileDialog = null,
        DbcEncodeService? encodeService = null)
    {
        _svc = svc ?? throw new ArgumentNullException(nameof(svc));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _fileDialog = fileDialog ?? new WpfFileDialogService();
        SuiteBuilder = new TestSuiteBuilderViewModel(svc, logger, _fileDialog, encodeService);
        EcuSimulator = new EcuSimulatorViewModel(logger, _fileDialog);
        _svc.DbcLoaded += OnLoaded;
        _svc.LoadFailed += OnLoadFailed;
    }

    /// <summary>切消息时清掉残留的信号选择 + 重建信号 grid（防御嵌套 DataGrid 重载写回 null）。</summary>
    partial void OnSelectedMessageChanged(HilStudioDbcMessageRow? value)
    {
        SelectedSignal = null;
        UpdateVisibleSignals();
    }
}
