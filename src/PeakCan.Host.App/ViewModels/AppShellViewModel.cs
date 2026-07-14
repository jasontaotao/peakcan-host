using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PeakCan.Host.App.Composition;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.Views;
using PeakCan.Host.App.ViewModels.Uds;
using PeakCan.Host.App.Windows;
using PeakCan.Host.Core;
using PeakCan.Host.Infrastructure.Channel;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// Top-level shell view model: status bar, channel-probe / connect toolbar,
/// the Open-DBC menu command, and the per-tab view cache that drives the
/// <c>MainArea</c> <see cref="System.Windows.Controls.ContentControl"/>.
/// <para>
/// <b>View caching (Task 15):</b> the three tab views (<see cref="TraceView"/>,
/// <see cref="DbcView"/>, <see cref="SendView"/>) are instantiated once in
/// the constructor and swapped via <see cref="CurrentView"/>. Reusing the
/// view instances preserves DataGrid virtualization state across switches
/// (scroll position, selection) and avoids paying the DataGrid layout cost
/// on every menu click. Each view's <c>DataContext</c> is bound at
/// construction time so XAML bindings resolve without any per-click
/// DataContext plumbing.
/// </para>
/// <para>
/// <b>Hardware probe (MVP):</b> per the inline amendment, this class
/// hard-codes handle <c>0x51</c> (PCAN-USB FD first channel) and probes
/// it with <c>PCANBasic.Initialize</c>. A non-error status means the
/// channel is reachable and <see cref="ConnectCommand"/> is enabled.
/// Full multi-channel enumeration is v1.1.
/// </para>
/// <para>
/// <b>Thread-safety:</b> <see cref="ConnectAsync"/> captures the WPF
/// <c>DispatcherSynchronizationContext</c> at the await site via
/// <c>ConfigureAwait(true)</c>; the property setters below it run on the
/// UI thread because the await resumed there. CommunityToolkit.Mvvm's
/// generated <c>SetProperty</c> itself does NOT marshal — it just fires
/// <c>PropertyChanged</c>. The dispatcher affinity comes from the captured
/// context, not from the source generator.
/// </para>
/// </summary>
public sealed partial class AppShellViewModel : ObservableObject
{
    /// <summary>
    /// PEAK PCAN-USB FD first channel handle. Mirrors
    /// <see cref="Composition.AppHostBuilder.PcanUsbFdFirstHandle"/>; kept
    /// here as a local constant so the VM does not pull in App composition
    /// for a single number.
    /// </summary>
    private const ushort PcanUsbFdFirstHandle = 0x51;

    /// <summary>窗口标题，含版本号（从 AssemblyInformationalVersion 读取）。</summary>
    public string WindowTitle { get; } = $"PeakCan Host v{Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion ?? "0.0.0"}";

    /// <summary>Classic CAN 预设列表（125 / 250 / 500 / 1000 kbps）。</summary>
    public static readonly IReadOnlyList<BaudRate> ClassicBaudRates =
        new[] { BaudRate.Can125kbps, BaudRate.Can250kbps, BaudRate.Can500kbps, BaudRate.Can1Mbps };

    /// <summary>CAN FD 预设列表（1 / 2 / 5 Mbps data phase）。</summary>
    public static readonly IReadOnlyList<BaudRate> FdBaudRates =
        new[] { BaudRate.CanFd1Mbps, BaudRate.CanFd2Mbps, BaudRate.CanFd5Mbps };

    private readonly ChannelRouter _router;
    private readonly ILogger<AppShellViewModel> _logger;
    private readonly SendService _sendService;
    private readonly IChannelProbe _channelProbe;
    private readonly IChannelFactory _channelFactory;
    private readonly IChannelEnumerator? _channelEnumerator;
    private readonly TraceViewModel _traceViewModel;
    private readonly DbcViewModel _dbcViewModel;
    private readonly SendViewModel _sendViewModel;
    private readonly SignalViewModel _signalViewModel;
    private readonly StatsViewModel _statsViewModel;
    private readonly ScriptViewModel _scriptViewModel;
    private readonly UdsViewModel _udsViewModel;
    // v3.50.1 PATCH-A: RecordViewModel restored to AppShell ctor (reverts
    // v3.49 Q2 which moved Recording into Trace Viewer window Expander —
    // conflated playback with capture). AppHostBuilder wires the
    // RecordViewModel singleton through this field; ShowRecordCommand
    // opens RecordView via ViewSwitcher.Show (same lazy-create +
    // cache-resume pattern as the other View commands).
    private readonly RecordViewModel _recordViewModel;
    // v2.1.4 PATCH: Replay tab was orphaned since v1.4.0 MINOR — ReplayViewModel
    // exists (with the full ReplayView UI behind it) but no AppShell-level
    // navigation route reached it. Wiring the VM through DI + ctor is the first
    // half of the fix; AppShell.xaml menu entry is the second half.
    private readonly ReplayViewModel _replayViewModel;
    // v2.1.7 PATCH: Multi-frame send window was reachable only via the
    // SendView button (Pattern A2 orphan since v2.1.0 MINOR). AppShell now
    // holds the shared MultiFrameSendViewModel and opens a new
    // MultiFrameSendWindow on menu click. SendViewModel keeps its own
    // independent window instance — both point at the same singleton VM.
    private readonly MultiFrameSendViewModel _multiFrameSendViewModel;
    // v3.0 MINOR Task 7: Trace Viewer non-modal window (Pattern A orphan
    // closure — TraceViewerView + VM + ITraceViewerService were fully
    // built in Tasks 1-6 but AppShell had no menu route). Shared singleton
    // VM so the menu, future SendView button, and any other consumer all
    // bind to the same loaded trace + signal list + chart scrubber state.
    private readonly TraceViewerViewModel _traceViewerViewModel;
    // v3.6.0 MINOR T3: MRU list backing the File ▸ Open Recent menu.
    // Singleton so multiple consumers (AppShell today, future shortcuts)
    // observe the same ordering; persisted to
    // %APPDATA%/PeakCan.Host/recent-sessions.json.
    private readonly RecentSessionsService _recentSessions;
    // v3.6.0 MINOR T3: file-dialog abstraction so the Save/Open Session
    // menu commands can be unit-tested with a fake (no WPF Application
    // needed). Production DI wires the WPF impl.
    private readonly IFileDialogService _fileDialogs;
    // v3.10.0 MINOR T1 (C1): IMessageBoxPrompt seam so the 2
    // missing-.asc modals in OpenSessionAsync / OpenRecentSessionAsync
    // can route through a testable abstraction (no WPF MessageBox.Show
    // at VM layer). Production DI wires the WPF impl.
    private readonly IMessageBoxPrompt _messageBoxPrompt;
    // v1.5.0 MINOR: persistence for SelectedChannel (Channel:SelectedHandle).
    private readonly IConfiguration _configuration;
    // v1.5.0 MINOR: persisted handle from ctor, applied on first EnumerateChannels.
    private ushort? _persistedHandleOnStartup;
    // v1.5.0 MINOR: when EnumerateChannels auto-selects a fallback because
    // the persisted handle did not match any enumerated channel, suppress
    // the subsequent OnSelectedChannelChanged write so we do not clobber
    // the user's original persisted value (e.g. "99" if the hardware no
    // longer matches). Cleared by the next real OnSelectedChannelChanged
    // invocation (which always persists user intent).
    private bool _suppressNextPersist;

    // View instances are created lazily on the first Show command so the
    // shell's ctor stays STA-free (xunit runs on MTA). Production callers
    // always resolve the VM from the WPF STA thread (App.OnStartup), so
    // the first Show happens on STA and the WPF UserControl ctor succeeds.
    private TraceView? _traceView;
    private DbcView? _dbcView;
    private SendView? _sendView;
    private SignalView? _signalView;
    private StatsView? _statsView;
    private ScriptView? _scriptView;
    private UdsWindow? _udsWindow;
    // v3.50.1 PATCH-A: RecordView cache field restored (reverts v3.49 Q2).
    private RecordView? _recordView;
    private ReplayView? _replayView;
    // v3.0 MINOR Task 7: TraceViewerView is a non-modal Window (not a
    // tab in the MainArea ContentControl), so it lives outside the
    // WPF View cache. Lazy + Closed-reset pattern mirrors the
    // OpenMultiFrame window precedent (each menu click reopens the
    // cached window without spawning a fresh one).
    private TraceViewerView? _traceViewerView;

    /// <summary>Active channel after a successful Connect command; null otherwise.</summary>
    private ICanChannel? _activeChannel;

    /// <summary>Last known probe result. Connect is enabled only when this is "USB1 ...".</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private string _channelList = "(click Probe to detect)";

    /// <summary>
    /// v0.4.0: detected channels from the last EnumerateChannels call.
    /// Empty before the first probe. The toolbar ComboBox binds to this.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private IReadOnlyList<ChannelInfo> _availableChannels = Array.Empty<ChannelInfo>();

    /// <summary>
    /// v0.4.0: the channel the user selected from the toolbar ComboBox.
    /// Null before the first probe or if no channels were detected.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private ChannelInfo? _selectedChannel;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private string _connectionState = "Disconnected";

    /// <summary>
    /// True after a successful Connect until a future Disconnect (v1.1).
    /// The toolbar binds to this for the enabled state of the
    /// Connect button; bound via the partial property generated by
    /// CommunityToolkit.Mvvm.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    [NotifyPropertyChangedFor(nameof(IsDisconnected))]
    private bool _isConnected;

    /// <summary>
    /// 与 <see cref="IsConnected"/> 相反，供工具栏 CAN FD / 波特率控件的
    /// <c>IsEnabled</c> 绑定——连接状态下禁用，断开后恢复。
    /// </summary>
    public bool IsDisconnected => !IsConnected;

    /// <summary>CAN FD 模式开关。切换时自动将 SelectedBaudRate 重置为对应列表首项。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvailableBaudRates))]
    private bool _isFd = true;

    /// <summary>当前选中的波特率预设。工具栏 ComboBox 绑定此属性。</summary>
    [ObservableProperty]
    private BaudRate _selectedBaudRate = BaudRate.CanFd1Mbps;

    /// <summary>
    /// The view currently shown in <c>AppShell.xaml</c>'s <c>MainArea</c>.
    /// Switched by the View menu (Trace / DBC / Send) and by the
    /// Open-DBC menu command. The instance is cached — see class doc.
    /// </summary>
    [ObservableProperty]
    private object? _currentView;

    /// <summary>
    /// v3.6.0 MINOR T3: XAML binding source for the File ▸ Open Recent
    /// submenu. Rebuilt from <see cref="RecentSessionsService.Recent"/>
    /// whenever the service raises <see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged"/>
    /// (Add / Remove / Clear / LoadAsync). The wrapper record
    /// <see cref="RecentSessionVm"/> carries only the two fields the
    /// menu needs (Label for display, Path as CommandParameter) —
    /// <see cref="RecentSessionDto.SavedAt"/> is consumed only in
    /// tooltip / future UX.
    /// </summary>
    public ObservableCollection<RecentSessionVm> RecentSessionEntries { get; } = new();

    /// <summary>v3.6.0 MINOR T3: lightweight VM-side projection of
    /// <see cref="RecentSessionDto"/> for the Open Recent submenu.
    /// <see cref="Path"/> is the CommandParameter for
    /// <c>OpenRecentSessionCommand</c>; <see cref="Label"/> is the menu
    /// header text. Mirrors the AppShell.xaml
    /// <c>DataTemplate</c> binding contract.</summary>
    public sealed record RecentSessionVm(string Path, string Label);

    /// <summary>根据 IsFd 动态返回可用波特率预设列表。ComboBox ItemsSource 绑定此属性。</summary>
    public IReadOnlyList<BaudRate> AvailableBaudRates => IsFd ? FdBaudRates : ClassicBaudRates;


    /// <summary>Manual-send service for shell-to-send tab wiring (Task 14).</summary>
    public SendService SendService => _sendService;

    public AppShellViewModel(
        ChannelRouter router,
        ILogger<AppShellViewModel> logger,
        TraceViewModel traceViewModel,
        SendService sendService,
        IChannelProbe channelProbe,
        IChannelFactory channelFactory,
        DbcViewModel dbcViewModel,
        SendViewModel sendViewModel,
        SignalViewModel signalViewModel,
        StatsViewModel statsViewModel,
        ScriptViewModel scriptViewModel,
        UdsViewModel udsViewModel,
        // v3.50.1 PATCH-A: RecordViewModel ctor arg restored (reverts v3.49 Q2).
        RecordViewModel recordViewModel,
        ReplayViewModel replayViewModel,
        MultiFrameSendViewModel multiFrameSendViewModel,
        TraceViewerViewModel traceViewerViewModel,
        RecentSessionsService recentSessions,
        IFileDialogService fileDialogs,
        // v3.10.0 MINOR T1 (C1): required ctor arg so the 2
        // missing-.asc MessageBox.Show call sites can route through
        // IMessageBoxPrompt — restoring VM unit testability. DI
        // always wires the WPF impl; test sites inject
        // Substitute.For<IMessageBoxPrompt>().
        IMessageBoxPrompt messageBoxPrompt,
        IChannelEnumerator? channelEnumerator = null,
        IConfiguration? configuration = null)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _traceViewModel = traceViewModel ?? throw new ArgumentNullException(nameof(traceViewModel));
        _sendService = sendService ?? throw new ArgumentNullException(nameof(sendService));
        _channelProbe = channelProbe ?? throw new ArgumentNullException(nameof(channelProbe));
        _channelFactory = channelFactory ?? throw new ArgumentNullException(nameof(channelFactory));
        _dbcViewModel = dbcViewModel ?? throw new ArgumentNullException(nameof(dbcViewModel));
        _sendViewModel = sendViewModel ?? throw new ArgumentNullException(nameof(sendViewModel));
        _signalViewModel = signalViewModel ?? throw new ArgumentNullException(nameof(signalViewModel));
        _statsViewModel = statsViewModel ?? throw new ArgumentNullException(nameof(statsViewModel));
        _scriptViewModel = scriptViewModel ?? throw new ArgumentNullException(nameof(scriptViewModel));
        _udsViewModel = udsViewModel ?? throw new ArgumentNullException(nameof(udsViewModel));
        // v3.50.1 PATCH-A: RecordViewModel field assignment restored.
        _recordViewModel = recordViewModel ?? throw new ArgumentNullException(nameof(recordViewModel));
        _replayViewModel = replayViewModel ?? throw new ArgumentNullException(nameof(replayViewModel));
        _multiFrameSendViewModel = multiFrameSendViewModel ?? throw new ArgumentNullException(nameof(multiFrameSendViewModel));
        // v3.0 MINOR Task 7: Trace Viewer non-modal window. Required ctor
        // argument (no default) so DI always wires it — backwards-
        // compatible test fixtures that build the VM without DI will
        // receive a Compile Error on missing arg, exactly the behaviour
        // we want for the v3.0 surface addition. Existing test sites
        // were updated to construct with a stub ITraceViewerService +
        // null DbcService substitute.
        _traceViewerViewModel = traceViewerViewModel ?? throw new ArgumentNullException(nameof(traceViewerViewModel));
        // v3.6.0 MINOR T3: MRU list + file-dialog wiring. The
        // RecentSessionsService is a singleton; subscribing to its
        // PropertyChanged keeps the AppShell menu in sync with
        // external mutations (e.g. AutoSave pre-populating the list
        // in a future change). Initial LoadAsync is fire-and-forget —
        // the service leaves the list empty until then, matching the
        // pre-load state.
        _recentSessions = recentSessions ?? throw new ArgumentNullException(nameof(recentSessions));
        _recentSessions.PropertyChanged += (_, __) => RefreshRecentEntries();
        _fileDialogs = fileDialogs ?? throw new ArgumentNullException(nameof(fileDialogs));
        // v3.10.0 MINOR T1 (C1): route the missing-.asc information
        // modals through the existing IMessageBoxPrompt seam so the
        // VM remains unit-testable (no WPF MessageBox.Show at VM
        // layer). The IFileDialogService parameter above follows the
        // same pattern — both are owner-bound modal abstractions.
        _messageBoxPrompt = messageBoxPrompt ?? throw new ArgumentNullException(nameof(messageBoxPrompt));
        // v3.6.0 MINOR T3: load the persisted MRU list at startup so
        // the File ▸ Open Recent submenu is populated. Fire-and-forget
        // — the service never throws on load (corrupt → empty list)
        // and the menu can render an empty Recent without a UX bug.
        // The PropertyChanged subscription above handles the rebuild
        // when LoadAsync raises the change notification synchronously;
        // no explicit ContinueWith needed (T3 review M1/M2 fix).
        _ = _recentSessions.LoadAsync(CancellationToken.None);
        // v0.4.0: optional multi-channel enumerator. When null, the
        // single-channel probe path (IChannelProbe) is used instead.
        _channelEnumerator = channelEnumerator;
        // v1.5.0 MINOR: persist SelectedHandle across app restarts.
        // Configuration is optional for backwards compatibility with
        // existing test fixtures that build the VM without DI. The
        // fallback uses an in-memory provider so the indexer setter is
        // a no-op rather than throwing on an unregistered root.
        _configuration = configuration ?? new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        // Defer handle restoration to first EnumerateChannels call: the
        // persisted handle can only resolve against an enumerated
        // AvailableChannels list, which may be empty until the user
        // probes hardware.
        var persisted = _configuration["Channel:SelectedHandle"];
        if (!string.IsNullOrEmpty(persisted) &&
            ushort.TryParse(persisted, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h))
        {
            _persistedHandleOnStartup = h;
        }
    }


    // LoggerMessage source-generated helpers silence CA1848 (use LoggerMessage
    // source generators) and CA1873 (avoid expensive arg computation in
    // disabled loggers). The methods are deliberately not called from hot
    // paths; their only call site is the VM commands.

    // === Flow D methods moved to AppShellViewModel/LogFlow.cs (W4 Task 1) ===
    // === Flow C methods moved to AppShellViewModel/SessionFlow.cs (W4 Task 2) ===
    // === Flow B methods moved to AppShellViewModel/ViewSwitchFlow.cs (W4 Task 3) ===
    // === Flow A methods moved to AppShellViewModel/ChannelFlow.cs (W4 Task 4) ===
}
