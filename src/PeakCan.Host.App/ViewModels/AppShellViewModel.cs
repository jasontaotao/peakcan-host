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

    /// <summary>
    /// IsFd 属性变更回调：切换模式时自动将 SelectedBaudRate 重置为对应列表首项，
    /// 避免用户在 Classic 模式下残留一个 FD 预设（或反之）。
    /// CommunityToolkit.Mvvm 源生成器会将此方法注册到 IsFd 的 setter 中。
    /// </summary>
    partial void OnIsFdChanged(bool value)
    {
        SelectedBaudRate = value ? BaudRate.CanFd1Mbps : BaudRate.Can1Mbps;
    }

    /// <summary>
    /// v1.5.0 MINOR: persist <c>SelectedChannel.Handle</c> to
    /// <c>Channel:SelectedHandle</c> in <see cref="IConfiguration"/> so the
    /// next process restart can restore the previously-selected channel
    /// after EnumerateChannels populates <see cref="AvailableChannels"/>.
    /// Handle format is uppercase hex without 0x prefix (matches PEAK
    /// convention: 0x51 → "51"). A null SelectedChannel clears the key.
    /// <para>
    /// v1.5.0 review fix: when <see cref="EnumerateChannels"/> auto-selects
    /// a fallback (the persisted handle did not match any enumerated channel),
    /// <see cref="_suppressNextPersist"/> is set so this write is skipped,
    /// preserving the user's original persisted value across the hardware
    /// mismatch. Any subsequent user-driven selection always persists.
    /// </para>
    /// </summary>
    partial void OnSelectedChannelChanged(ChannelInfo? value)
    {
        if (_suppressNextPersist)
        {
            // Consume the flag for this single auto-select event; the very
            // next user-driven change will persist normally.
            _suppressNextPersist = false;
            return;
        }
        _configuration["Channel:SelectedHandle"] = value?.Handle.ToString("X2");
    }

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

    [RelayCommand]
    private void OpenDbc()
    {
        // Task 15: the File ▸ Open DBC... menu item routes into the DBC
        // tab. The actual file-open dialog is owned by the per-view
        // Open button inside DbcViewModel.OpenAsync; the menu item
        // only navigates so the user sees the right surface.
        CurrentView = GetOrCreateDbcView();
        LogOpenDbcInvoked(_logger);
    }

    /// <summary>
    /// v3.6.0 MINOR T3: File ▸ Open Session... menu command. Pops a
    /// file-open dialog (via the WPF-independent <see cref="IFileDialogService"/>),
    /// loads the chosen bundle through <see cref="TraceViewerViewModel.OpenSessionAsync"/>,
    /// surfaces any missing <c>.asc</c> recordings via MessageBox, and
    /// records the path in the MRU list. Cancellation returns silently.
    /// </summary>
    [RelayCommand]
    private async Task OpenSessionAsync()
    {
        var path = _fileDialogs.ShowOpenDialog(
            "Trace Viewer session|*.tmtrace;*.TMTRACE|All files|*.*");
        if (string.IsNullOrEmpty(path)) return;
        var missing = await _traceViewerViewModel.OpenSessionAsync(path)
            .ConfigureAwait(true);
        if (missing.Count > 0)
        {
            // v3.10.0 MINOR T1 (C1): route through IMessageBoxPrompt
            // instead of MessageBox.Show so the VM stays unit-testable.
            // The WPF impl mirrors the previous OK + Warning image
            // contract; tests substitute a fake to assert invocation.
            await _messageBoxPrompt.ShowInformationAsync(
                "Open Session",
                $"These .asc files are missing:\n{string.Join("\n", missing)}",
                Application.Current?.MainWindow)
                .ConfigureAwait(true);
        }
        _recentSessions.Add(path, "trace");
    }

    /// <summary>
    /// v3.6.0 MINOR T3: File ▸ Save Session... menu command. Pops a
    /// file-save dialog, hands the chosen path to
    /// <see cref="TraceViewerViewModel.SaveSessionAsync"/>, then records
    /// it in the MRU list. The Trace Viewer window must be open and
    /// hold the session state being saved; we do not auto-open it here
    /// (matches the toolbar behaviour that the menu is replacing).
    /// </summary>
    [RelayCommand]
    private async Task SaveSessionAsync()
    {
        var path = _fileDialogs.ShowSaveDialog(
            "Trace Viewer session|*.tmtrace|All files|*.*",
            ".tmtrace",
            null);
        if (string.IsNullOrEmpty(path)) return;
        await _traceViewerViewModel.SaveSessionAsync(path)
            .ConfigureAwait(true);
        _recentSessions.Add(path, "trace");
    }

    /// <summary>
    /// v3.6.0 MINOR T3: File ▸ Open Recent ▸ &lt;name&gt; menu command.
    /// <paramref name="path"/> is the CommandParameter wired through
    /// the <c>DataTemplate</c> in <c>AppShell.xaml</c>. Skips the file
    /// dialog (the path was chosen from the MRU), forwards to
    /// <see cref="TraceViewerViewModel.OpenSessionAsync"/>, and
    /// re-records the path so a re-click moves it back to the top of
    /// the list (matching standard MRU UX).
    /// </summary>
    [RelayCommand]
    private async Task OpenRecentSessionAsync(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        var missing = await _traceViewerViewModel.OpenSessionAsync(path)
            .ConfigureAwait(true);
        if (missing.Count > 0)
        {
            // v3.10.0 MINOR T1 (C1): see OpenSessionAsync — same
            // IMessageBoxPrompt seam, distinct title so the user
            // can tell which menu path triggered the warning.
            await _messageBoxPrompt.ShowInformationAsync(
                "Open Recent Session",
                $"These .asc files are missing:\n{string.Join("\n", missing)}",
                Application.Current?.MainWindow)
                .ConfigureAwait(true);
        }
        _recentSessions.Add(path, "trace");
    }

    /// <summary>
    /// v3.6.0 MINOR T3: File ▸ Clear Recent menu command. Wipes the
    /// Trace entries only — replay entries (added by the Replay tab's
    /// own submenu in chunk 2) survive. The on-disk JSON file is left
    /// alone unless the list became empty as a side effect.
    /// </summary>
    [RelayCommand]
    private void ClearRecentSessions() => _recentSessions.Clear("trace");

    /// <summary>
    /// v3.6.0 MINOR T3: rebuild <see cref="RecentSessionEntries"/> from
    /// <see cref="RecentSessionsService.Recent"/>. Called on
    /// <see cref="RecentSessionsService"/> PropertyChanged (any
    /// mutation) and once after the initial LoadAsync. Cheap (max 5
    /// entries) — full Clear + rebuild avoids the per-item
    /// CollectionChanged dance.
    /// <para>
    /// v3.7.0 MINOR Chunk 2: the AppShell menu now filters to Trace
    /// entries only. Empty <c>ViewType</c> is the legacy-trace value
    /// carried over from v3.6.0–v3.6.4 entries (which pre-date the
    /// field); treating it as trace preserves the user's pre-existing
    /// MRU list across the upgrade.
    /// </para>
    /// </summary>
    private void RefreshRecentEntries()
    {
        RecentSessionEntries.Clear();
        foreach (var r in _recentSessions.Recent)
        {
            // v3.7.0: filter to Trace Viewer entries only. Empty ViewType
            // is legacy-trace (v3.6.x saves) — kept for back-compat.
            if (r.ViewType != "trace" && r.ViewType != "")
                continue;
            RecentSessionEntries.Add(new RecentSessionVm(r.Path, r.Label));
        }
    }

    [RelayCommand]
    private void ShowTrace()
    {
        // v3.11.1 PATCH M3: extract the lazy-view-create / cache-resume
        // pattern into ViewSwitcher.Show. The original inline body
        // (4 lines including the first-show default-tab comment) is now
        // a single helper call. Show preserves the DataContext bind +
        // first-show CurrentView=null fallback behaviour (helper just
        // calls setCurrent).
        ViewSwitcher.Show(
            factory: () => new TraceView { DataContext = _traceViewModel },
            cache: ref _traceView,
            setCurrent: v => CurrentView = v,
            menuName: nameof(ShowTrace));
    }

    [RelayCommand]
    private void ShowDbc() => CurrentView = GetOrCreateDbcView();

    [RelayCommand]
    private void ShowSend()
    {
        // v3.11.1 PATCH M3: see ShowTrace — same ViewSwitcher extraction.
        ViewSwitcher.Show(
            factory: () => new SendView { DataContext = _sendViewModel },
            cache: ref _sendView,
            setCurrent: v => CurrentView = v,
            menuName: nameof(ShowSend));
    }

    [RelayCommand]
    private void ShowSignals()
    {
        // Task 16: Signal tab (DBC-decoded live signals). v3.11.1 PATCH M3:
        // extracted into ViewSwitcher — same lazy-create / cache-resume
        // behaviour, DataContext bind at first-show, DataGrid
        // virtualization state preserved across menu round-trips.
        ViewSwitcher.Show(
            factory: () => new SignalView { DataContext = _signalViewModel },
            cache: ref _signalView,
            setCurrent: v => CurrentView = v,
            menuName: nameof(ShowSignals));
    }

    [RelayCommand]
    private void ShowStats()
    {
        // Task 17: Stats tab (1 Hz OxyPlot charts). v3.11.1 PATCH M3:
        // extracted into ViewSwitcher — same lazy-create / cache-resume
        // behaviour. The StatsView hosts an OxyPlot.PlotView bound to
        // StatsViewModel.PlotModel; the StatisticsService pushes snapshots
        // at 1 Hz on its own thread and the VM marshals to the UI
        // dispatcher.
        ViewSwitcher.Show(
            factory: () => new StatsView { DataContext = _statsViewModel },
            cache: ref _statsView,
            setCurrent: v => CurrentView = v,
            menuName: nameof(ShowStats));
    }

    [RelayCommand]
    private void ShowScript()
    {
        // v1.0.0: Script tab (JavaScript automation). v3.11.1 PATCH M3:
        // extracted into ViewSwitcher. The ScriptView hosts a WebView2
        // with CodeMirror 6 editor and an output panel.
        ViewSwitcher.Show(
            factory: () => new ScriptView { DataContext = _scriptViewModel },
            cache: ref _scriptView,
            setCurrent: v => CurrentView = v,
            menuName: nameof(ShowScript));
    }

    [RelayCommand]
    private void ShowUds()
    {
        // v3.11.3 PATCH: UDS migrated from an in-place UserControl tab to
        // a separate non-modal Window. Mirrors the v3.9.1 PATCH B1 + v3.11.1
        // PATCH M3 secondary-window precedent established by ShowTraceViewer:
        // factory + cache lifecycle owned by ViewSwitcher.ShowWindow
        // (auto Closed-reset); Owner + Show/Activate owned by the caller
        // (Application.Current.MainWindow only resolves inside App.OnStartup's
        // STA context).
        //
        // Behaviour parity with the pre-PATCH UserControl path:
        // - First Show creates the window from the factory.
        // - Second Show reuses the cached instance (window position + size +
        //   SelectedDid + Did/Routine/Dtc selections all preserved).
        // - Closing the window clears the cache so the next Show opens fresh.
        // - Closing AppShell cascade-closes the UDS window via the Owner
        //   assignment below (mirrors ShowTraceViewer at line 681).
        ViewSwitcher.ShowWindow(
            factory: () => new UdsWindow { DataContext = _udsViewModel },
            cache: ref _udsWindow);
        if (_udsWindow is null) return; // defensive — cache cannot be null after ShowWindow

        if (Application.Current?.MainWindow is { } owner && owner != _udsWindow)
            _udsWindow.Owner = owner;

        if (!_udsWindow.IsVisible)
        {
            _udsWindow.Show();
        }
        else
        {
            // Already shown — bring to the foreground instead of re-activating
            // (which on Windows flashes the taskbar icon for an already-visible
            // window and looks like a bug). Same precedent as ShowTraceViewer.
            _udsWindow.Activate();
        }
    }

    [RelayCommand]
    private void ShowRecord()
    {
        // v1.2.11 PATCH Item 6: Recording tab. v3.11.1 PATCH M3:
        // extracted into ViewSwitcher — view is constructed on first Show
        // so the shell ctor stays STA-free.
        ViewSwitcher.Show(
            factory: () => new RecordView { DataContext = _recordViewModel },
            cache: ref _recordView,
            setCurrent: v => CurrentView = v,
            menuName: nameof(ShowRecord));
    }

    [RelayCommand]
    private void ShowReplay()
    {
        // v2.1.4 PATCH: Replay tab (closes the v1.4.0 MINOR orphan).
        // v3.11.1 PATCH M3: extracted into ViewSwitcher. The tab was
        // fully built (ReplayView + ReplayViewModel + IReplayService +
        // tests) but AppShell had no ShowReplayCommand and AppHostBuilder
        // had no ReplayViewModel DI registration, so the tab was
        // unreachable. ViewSwitcher.Show preserves the same lazy-create
        // + cache-resume behaviour.
        ViewSwitcher.Show(
            factory: () => new ReplayView { DataContext = _replayViewModel },
            cache: ref _replayView,
            setCurrent: v => CurrentView = v,
            menuName: nameof(ShowReplay));
    }

    [RelayCommand]
    private void OpenMultiFrame()
    {
        // v2.1.7 PATCH: Multi-frame send window from the AppShell View
        // menu. Closes the v2.1.0 MINOR Pattern A2 orphan — the window
        // + VM were fully built and SendView held a button to open it,
        // but AppShell had no menu route. Each menu click opens a fresh
        // window instance pointing at the shared singleton VM (matches
        // SendViewModel's lazy-show pattern; if both menus are used,
        // two independent windows coexist — acceptable for this PATCH;
        // window-state consolidation is a separate refactor).
        // v3.11.1 PATCH M3 spec notes OpenMultiFrame as one of the 3
        // secondary-window commands using the ShowWindow path, but the
        // current behaviour opens a FRESH window on every click (no
        // cache) — preserving that semantics here means a plain
        // factory invocation is correct. If a future PATCH wants to
        // cache the window, swap to ViewSwitcher.ShowWindow with a
        // nullable cache field (matches the Trace Viewer precedent).
        var win = new MultiFrameSendWindow(_multiFrameSendViewModel);
        if (Application.Current?.MainWindow is { } owner && owner != win)
            win.Owner = owner;
        win.Show();
    }

    [RelayCommand]
    private void ShowTraceViewer()
    {
        // v3.0 MINOR Task 7: Trace Viewer non-modal window from the
        // AppShell View menu. Closes the v3.0 Pattern A orphan —
        // TraceViewerView + TraceViewerViewModel + ITraceViewerService
        // were all built in Tasks 1-6 but AppShell had no menu route.
        // **No bus writes**: this is a read-only inspection surface
        // over the loaded ASC + optional DBC. Reuses the OpenMultiFrame
        // lazy-cached-window pattern (each menu click re-shows the
        // cached window; closing resets the reference so the next
        // click opens a fresh window). The window is non-modal and not
        // owned by AppShell so the user can keep the ASC open while
        // interacting with the main tabs.
        // v3.11.1 PATCH M3: factory + cache lifecycle extracted into
        // ViewSwitcher.ShowWindow. The helper wires the Closed-reset
        // automatically (v3.9.1 PATCH B1 pattern) so the explicit
        // Closed subscription is gone. Owner assignment + Show/Activate
        // stay here because they need Application.Current.MainWindow,
        // which only resolves inside App.OnStartup's STA context.
        ViewSwitcher.ShowWindow(
            factory: () => new TraceViewerView(_traceViewerViewModel),
            cache: ref _traceViewerView);
        if (_traceViewerView is null) return; // defensive — cache cannot be null after ShowWindow

        // v3.13.0 PATCH F2: hook the window's Closed event to clear
        // the singleton VM's mutable UI state on close. The VM is
        // shared with OpenSessionAsync / SaveSessionAsync (File menu),
        // so we cannot swap it per open — instead we reset its
        // observable state when the user closes the Trace Viewer
        // window. ViewSwitcher subscribes its OWN Closed handler to
        // null the cache; both fire (order doesn't matter).
        _traceViewerView.Closed += (_, _) => _traceViewerViewModel.Reset();

        // v3.9.1 PATCH Bug #1: set Owner = AppShell so closing the
        // main window cascade-closes the Trace Viewer. Without
        // Owner, Trace Viewer is an owner-less top-level Window;
        // WPF's default ShutdownMode=OnLastWindowClose keeps the
        // dispatcher running while Trace Viewer is visible, so the
        // user sees Trace Viewer survive AppShell close. Mirrors
        // OpenMultiFrame and SendViewModel's OpenMultiFrameSend
        // (SendViewModel.cs:522-525) — both already set Owner.
        // Application.Current.MainWindow is assigned to AppShell in
        // App.OnStartup.
        if (Application.Current?.MainWindow is { } owner && owner != _traceViewerView)
            _traceViewerView.Owner = owner;

        // v3.16.6 PATCH BUGFIX (defense-in-depth): WPF does not expose
        // a public IsClosed bool on Window; the "still alive" check is
        // membership in Application.Current.Windows. A closed window
        // has been removed from the collection. If we somehow hold a
        // closed reference here, drop it and let the next click rebuild.
        if (Application.Current?.Windows.Cast<Window>()
                .Any(w => ReferenceEquals(w, _traceViewerView)) != true)
        {
            _traceViewerView = null;
            return;
        }

        if (!_traceViewerView.IsVisible)
        {
            _traceViewerView.Show();
        }
        else
        {
            // Already shown — bring to the foreground instead of
            // re-activating (which on Windows flashes the taskbar
            // icon for an already-visible window and looks like a bug).
            _traceViewerView.Activate();
        }
    }

    private DbcView GetOrCreateDbcView() => _dbcView ??= new DbcView { DataContext = _dbcViewModel };

    [RelayCommand(CanExecute = nameof(CanEnumerateChannels))]
    private void EnumerateChannels()
    {
        // v0.4.0: if IChannelEnumerator is available, probe all channels;
        // otherwise fall back to the single-channel IChannelProbe path.
        if (_channelEnumerator is not null)
        {
            var channels = _channelEnumerator.Enumerate();
            AvailableChannels = channels;
            if (channels.Count > 0)
            {
                // v1.5.0 MINOR: if the user previously selected a different
                // channel and that channel is still present in the
                // enumerated list, restore it. Otherwise fall back to the
                // v0.4.0 default (channels[0]).
                var persisted = _persistedHandleOnStartup;
                _persistedHandleOnStartup = null; // consume once
                var match = persisted.HasValue
                    ? channels.FirstOrDefault(c => c.Handle == persisted.Value)
                    : null;
                // v1.5.0 review fix: when the persisted handle did not
                // match any enumerated channel (e.g. "99" but only 0x51/0x52
                // present), the auto-select below would otherwise trigger
                // OnSelectedChannelChanged and overwrite the user's persisted
                // "99" with "51". Suppress that one write so the user's
                // original intent survives across hardware changes.
                if (persisted.HasValue && match is null)
                {
                    _suppressNextPersist = true;
                }
                SelectedChannel = match ?? channels[0];
                ChannelList = $"{SelectedChannel.Name} ({SelectedBaudRate.Name})";
                StatusMessage = $"Detected {channels.Count} channel(s)";
                LogProbeOk(_logger, SelectedChannel.Handle);
            }
            else
            {
                SelectedChannel = null;
                ChannelList = "No PEAK hardware detected";
                StatusMessage = "No channels found";
                LogProbeThrew(_logger, PcanUsbFdFirstHandle,
                    new InvalidOperationException("No channels found"));
            }
        }
        else
        {
            // Legacy single-channel path (tests without IChannelEnumerator).
            var result = _channelProbe.Probe(PcanUsbFdFirstHandle);
            if (result.Ok)
            {
                ChannelList = $"USB1 ({SelectedBaudRate.Name})";
                StatusMessage = result.Message;
                LogProbeOk(_logger, PcanUsbFdFirstHandle);
            }
            else
            {
                ChannelList = $"No PEAK hardware detected: {result.Message}";
                StatusMessage = result.Message;
                LogProbeThrew(_logger, PcanUsbFdFirstHandle,
                    new InvalidOperationException(result.Message));
            }
        }
    }

    private bool CanEnumerateChannels() => !IsConnected;

    // v0.4.0: CanConnect now checks SelectedChannel when available,
    // falling back to the legacy ChannelList string check.
    private bool CanConnect() => !IsConnected && (
        SelectedChannel is not null
        || ChannelList.StartsWith("USB1", StringComparison.Ordinal));

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        // v0.4.0: use SelectedChannel handle when available.
        var handle = SelectedChannel?.Handle ?? PcanUsbFdFirstHandle;
        ConnectionState = "Connecting...";
        StatusMessage = $"Connecting to {SelectedChannel?.Name ?? "USB1"} ({SelectedBaudRate.Name})";
        var channel = _channelFactory.Create(new ChannelId(handle));
        try
        {
            var result = await channel.ConnectAsync(SelectedBaudRate, fd: IsFd).ConfigureAwait(true);
            if (result.IsSuccess)
            {
                _activeChannel = channel;
                _router.RegisterChannel(channel);
                // v3.16.9.4 PATCH: subscribe to read-loop errors so bus-off /
                // driver unload / hardware faults surface on the UI status
                // bar. Event fires on the SDK read thread; the handler must
                // marshal to the UI thread itself (we use the captured sync
                // context — same pattern as TraceViewerViewModel.OnAnyFrameEmitted).
                channel.ReadLoopError += OnReadLoopError;
                // Set IsConnected=true BEFORE publishing the channel to
                // SendService so that any binding observer sees "connected"
                // and an available channel atomically — no window where
                // Send can fire against a channel the UI still considers
                // disconnected. [ObservableProperty] setters fire
                // PropertyChanged in order; this ordering keeps the
                // Send button's CanExecute (when wired) consistent.
                IsConnected = true;
                ConnectionState = $"Connected to {SelectedChannel?.Name ?? "USB1"} ({SelectedBaudRate.Name})";
                StatusMessage = "Connected";
                _sendService.ActiveChannel = channel;
                LogConnectOk(_logger, handle);
            }
            else
            {
                ConnectionState = "Disconnected";
                _sendService.ActiveChannel = null;
                var err = result.Error!;
                StatusMessage = $"Connect failed: {err.Code} {err.Message}";
                LogConnectFailed(_logger, handle, err.Code, err.Message);
                // PeakCanChannel ctor allocates a CancellationTokenSource
                // (used by the read loop). On a failed Connect the channel
                // never acquires the hardware, so the safe teardown is to
                // dispose it now rather than wait for GC.
                await channel.DisposeAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            ConnectionState = "Disconnected";
            StatusMessage = $"Connect exception: {ex.GetType().Name}";
            LogConnectThrew(_logger, handle, ex);
            // v3.8.8 PATCH F1: also unregister the channel from the
            // router. RegisterChannel is a two-step operation in
            // ChannelRouter (Add to _channels + event subscribe); if
            // the subscribe step throws AFTER the Add, the channel
            // stays in the router's sink list and frames keep fanning
            // into a disposed sink. UnregisterChannel is idempotent
            // (Remove is a no-op if the channel was never added), so
            // it is safe to call on every catch. Best-effort wrapped
            // so a router failure cannot prevent the channel dispose.
            try { _router.UnregisterChannel(channel); }
            catch (Exception unregEx)
            {
                LogUnregisterFailed(_logger, handle, unregEx);
            }
            // M1 fix: dispose the channel if RegisterChannel or any
            // subsequent step threw after ConnectAsync succeeded. Without
            // this, the channel (and its CTS + read-loop task) leaks until
            // the next GC cycle.
            await channel.DisposeAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        if (!IsConnected || _activeChannel is null) return;
        StatusMessage = $"Disconnecting from {SelectedBaudRate.Name}";
        ConnectionState = "Disconnecting...";
        try
        {
            await _activeChannel.DisconnectAsync().ConfigureAwait(true);
            _router.UnregisterChannel(_activeChannel);
            // v3.16.9.4 PATCH: unsubscribe from read-loop errors before
            // dropping the channel reference. Without this, a subsequent
            // Connect → Disconnect cycle would leave the old channel's
            // ReadLoopError event holding a strong reference to this VM
            // (closure pins the captured `this`). Match the source-gen
            // delegate equality used by PeakCanChannel.ReadLoopError.
            _activeChannel.ReadLoopError -= OnReadLoopError;
            _sendService.ActiveChannel = null;
            IsConnected = false;
            ConnectionState = "Disconnected";
            StatusMessage = "Disconnected";
            LogDisconnectOk(_logger, PcanUsbFdFirstHandle);
        }
        catch (Exception ex)
        {
            // DisconnectAsync swallows hardware failures per its own contract;
            // any exception here is therefore unexpected. Surface it as a
            // status message so the operator is not stuck in "Disconnecting".
            // Reset every piece of state the success path resets: leaving
            // IsConnected=true keeps the Disconnect button enabled against a
            // dead channel; leaving the channel on the router keeps frames
            // being routed to it; leaving SendService.ActiveChannel set
            // targets the next manual send at a dead channel. Order matches
            // the success path (UnregisterChannel → ActiveChannel=null →
            // IsConnected=false) so the two paths produce identical state
            // transitions from an observer's point of view.
            _router.UnregisterChannel(_activeChannel);
            _sendService.ActiveChannel = null;
            IsConnected = false;
            ConnectionState = "Disconnected";
            StatusMessage = $"Disconnect exception: {ex.GetType().Name}";
            LogDisconnectThrew(_logger, PcanUsbFdFirstHandle, ex);
        }
        finally
        {
            _activeChannel = null;
        }
    }

    private bool CanDisconnect() => IsConnected;

    /// <summary>
    /// v3.16.9.4 PATCH: handler for <see cref="ICanChannel.ReadLoopError"/>.
    /// Fires on the SDK read thread; we marshal to the UI thread by setting
    /// <see cref="StatusMessage"/> via the [ObservableProperty] source-gen
    /// setter (which raises PropertyChanged on the captured sync context —
    /// or directly if no sync context, matching the same pattern as
    /// <see cref="TraceViewerViewModel.OnAnyFrameEmitted"/>).
    /// <para>
    /// The handler does NOT auto-disconnect — bus-off is often transient
    /// (PCANBasic automatically re-enters ERROR_ACTIVE after the bus
    /// recovers). Surfacing the error gives the operator the information
    /// to decide; the read loop's existing MaxConsecutiveReadFailures=100
    /// give-up mechanism handles the genuinely-dead-bus case.
    /// </para>
    /// </summary>
    private void OnReadLoopError(ReadLoopError err)
    {
        var msg = err.Kind switch
        {
            ReadLoopErrorKind.ClassicReadException =>
                $"Read loop error (classic): {err.Exception?.Message ?? "(no exception)"} — bus may be off",
            ReadLoopErrorKind.FdReadException =>
                $"Read loop error (FD): {err.Exception?.Message ?? "(no exception)"} — driver may be unloaded",
            ReadLoopErrorKind.LoopGivingUp =>
                $"Read loop abandoned after 100 failures — call Disconnect + Connect to recover",
            _ => $"Read loop error: kind={err.Kind}",
        };
        // Mark StatusMessage as the error message; the toolbar binding picks
        // it up. (YAGNI for a separate red-color binding — the StatusMessage
        // already conveys the error and the operator can correlate with the
        // "connected but no frames" symptom.)
        StatusMessage = msg;
        ConnectionState = $"Connected (read loop degraded: {err.Kind})";
        LogReadLoopError(_logger, err.Handle, err.Kind.ToString(), err.Exception);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Read loop error surfaced to UI: handle=0x{Handle:X2} kind={Kind}")]
    private static partial void LogReadLoopError(ILogger logger, ushort handle, string kind, Exception? ex);

    // LoggerMessage source-generated helpers silence CA1848 (use LoggerMessage
    // source generators) and CA1873 (avoid expensive arg computation in
    // disabled loggers). The methods are deliberately not called from hot
    // paths; their only call site is the VM commands.

    [LoggerMessage(Level = LogLevel.Information, Message = "Open DBC menu invoked")]
    private static partial void LogOpenDbcInvoked(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Probe OK on handle 0x{Handle:X2}")]
    private static partial void LogProbeOk(ILogger logger, ushort handle);

    [LoggerMessage(Level = LogLevel.Error, Message = "Probe threw on handle 0x{Handle:X2}")]
    private static partial void LogProbeThrew(ILogger logger, ushort handle, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connect OK on handle 0x{Handle:X2}")]
    private static partial void LogConnectOk(ILogger logger, ushort handle);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connect failed on handle 0x{Handle:X2}: {Code} {Message}")]
    private static partial void LogConnectFailed(ILogger logger, ushort handle, ErrorCode code, string message);

    [LoggerMessage(Level = LogLevel.Error, Message = "Connect threw on handle 0x{Handle:X2}")]
    private static partial void LogConnectThrew(ILogger logger, ushort handle, Exception ex);

    // v3.8.8 PATCH F1: best-effort wrapper for the catch-arm
    // UnregisterChannel call. If the router itself throws (e.g. lock
    // contention or another sink's DisposeAsync propagating), we log
    // and continue so the channel dispose still runs.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Connect catch-arm UnregisterChannel threw on handle 0x{Handle:X2}")]
    private static partial void LogUnregisterFailed(ILogger logger, ushort handle, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Disconnect OK on handle 0x{Handle:X2}")]
    private static partial void LogDisconnectOk(ILogger logger, ushort handle);

    [LoggerMessage(Level = LogLevel.Error, Message = "Disconnect threw on handle 0x{Handle:X2}")]
    private static partial void LogDisconnectThrew(ILogger logger, ushort handle, Exception ex);
}
