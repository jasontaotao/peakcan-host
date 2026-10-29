using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PeakCan.Host.App.Services;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// View-model for the DBC tab (<c>DbcView.xaml</c>). Owns the
/// <see cref="Messages"/> collection bound to the DataGrid and a
/// status string surfaced next to the Open button.
/// <para>
/// <b>Event wiring:</b> subscribes to <see cref="DbcService.DbcLoaded"/>
/// and <see cref="DbcService.LoadFailed"/> in the constructor. The
/// shell registers the VM as a DI singleton that lives for the whole
/// app, so the event subscriptions are intentionally NEVER unsubscribed
/// — a previous <see cref="IDisposable"/> implementation was a latent
/// footgun (see review Task 15 fix-history). Both <c>DbcService</c> and
/// <c>DbcViewModel</c> die together at process exit, so GC + finalizer
/// pass handles cleanup.
/// </para>
/// <para>
/// <b>Dispatcher marshaling:</b> <see cref="DbcService.LoadAsync"/> runs
/// the parse on a worker thread and raises the events on that worker.
/// Any mutation of an <see cref="System.Collections.ObjectModel.ObservableCollection{T}"/>
/// bound to a WPF <c>ItemsControl</c> MUST happen on the UI dispatcher
/// (cross-thread <c>CollectionChanged</c> throws
/// <c>NotSupportedException</c>). Both <see cref="OnLoaded"/> and
/// <see cref="OnLoadFailed"/> marshal via <see cref="System.Windows.Application.Current"/>.
/// In the xunit test context (no <c>Application</c>) we fall back to
/// running inline so the test can still observe the post-state.
/// </para>
/// <para>
/// <b>OpenCommand testability:</b> the production path pops a WPF
/// <see cref="OpenFileDialog"/>. The dialog itself cannot be exercised
/// from xunit (no STA / no <c>Application</c>); tests instead drive the
/// <see cref="DbcService"/> events directly to cover the resulting
/// state transitions.
/// </para>
/// </summary>
public sealed partial class DbcViewModel : ObservableObject
{
    private readonly DbcService _svc;
    private readonly SignalViewModel _signals;
    private readonly ILogger<DbcViewModel> _logger;
    private readonly IFileDialogService _fileDialog;
    private readonly IShellLauncher _shell;
    private readonly Func<string> _logDirectory;

    /// <summary>
    /// 最近一次**界面发起**的加载路径（打开 / 重试）。「重试」「打开目录」都靠它定位；
    /// 脚本侧（<c>DbcApi.Load</c>）触发的加载不写这里——那没有可重试的 UI 目标。
    /// </summary>
    private string? _lastOpenPath;

    /// <summary>
    /// 当前是否处在本类发起的加载调用里。<see cref="DbcService.LoadFailed"/> 可能在
    /// 脚本侧加载（<c>DbcApi.Load</c>）时触发，此时 <see cref="_lastOpenPath"/> 指向的是
    /// 上一次界面打开的文件——不区分就会让「重试」把另一个文件重新加载、覆盖当前文档。
    /// <para>
    /// 语义是**调用栈内**有效：<c>DbcService</c> 在 await 链里同步 raise 事件，
    /// 所以界面发起的失败一定能看到非零；调用返回之后才到的事件就是外来失败。
    /// </para>
    /// <para>
    /// 用计数而不是 bool：两次界面加载重叠时（A 在飞时又点了「打开」起 B），
    /// A 先结束会把 bool 清成 false，B 随后的失败就会被误判成脚本侧失败，
    /// 恢复入口被无辜关掉。计数只在整个栈退空时才归零。
    /// </para>
    /// </summary>
    private int _uiLoadInFlight;

    /// <summary>
    /// Rows bound to the DataGrid. Replaced on every successful load
    /// (Clear + Add) so the UI reflects the most recent parse.
    /// </summary>
    public ObservableCollection<DbcMessageViewModel> Messages { get; } = new();

    /// <summary>Last successfully loaded file path, or empty.</summary>
    [ObservableProperty]
    private string _loadedPath = "";

    /// <summary>Status string surfaced next to the Open button.</summary>
    [ObservableProperty]
    private string _status = "No DBC loaded";

    /// <summary>
    /// 最近一次加载失败的说明（null = 当前没有错误）。驱动 DbcView 顶部的错误条
    /// ——「重试 / 查看日志 / 打开目录」就在那条里。
    /// </summary>
    [ObservableProperty]
    private string? _loadError;

    /// <summary>
    /// Search text for filtering DBC messages. Matches message name
    /// or sender (case-insensitive substring). Empty = show all.
    /// </summary>
    [ObservableProperty]
    private string _searchText = "";

    // Full list of messages from the last successful load.
    private readonly List<DbcMessageViewModel> _allMessages = new();

    /// <summary>Filtered view of Messages based on SearchText.</summary>
    public ObservableCollection<DbcMessageViewModel> FilteredMessages { get; } = new();

    /// <summary>Total number of messages in the loaded DBC.</summary>
    [ObservableProperty]
    private int _totalMessages;

    /// <summary>Total number of signals across all messages.</summary>
    [ObservableProperty]
    private int _totalSignals;

    public DbcViewModel(DbcService svc, SignalViewModel signals, ILogger<DbcViewModel> logger,
                        IFileDialogService? fileDialog = null,
                        IShellLauncher? shellLauncher = null,
                        Func<string>? logDirectoryProvider = null)
    {
        _svc = svc ?? throw new ArgumentNullException(nameof(svc));
        _signals = signals ?? throw new ArgumentNullException(nameof(signals));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        // v0.7.0: optional file dialog service. When null, OpenAsync
        // falls back to the legacy WPF OpenFileDialog path (backwards
        // compatible with existing tests that don't inject one).
        _fileDialog = fileDialog ?? new WpfFileDialogService();
        // P1 防事故：加载失败恢复路径。两个都可用默认值，既有测试构造点不受影响。
        _shell = shellLauncher ?? new ExplorerShellLauncher();
        _logDirectory = logDirectoryProvider ?? (() => AppLogPaths.LogDirectory);
        _svc.DbcLoaded += OnLoaded;
        _svc.LoadFailed += OnLoadFailed;
    }


    [LoggerMessage(Level = LogLevel.Information, Message = "DBC Open invoked for {Path}")]
    private static partial void LogOpenInvoked(ILogger logger, string path);

}