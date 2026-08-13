using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Series;
using PeakCan.Host.App.Helpers;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.Trace;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Analysis;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.Replay;
using System.Collections.Specialized;
using ScottPlot;
using PeakCan.HIL.Core.Services;
using PeakCan.Host.App.Services.ChatTools;
using PeakCan.HIL.Core.Analysis.Chat;
namespace PeakCan.Host.App.ViewModels;


/// <summary>
/// v3.0 MINOR Trace Viewer: orchestration VM that bridges
/// <see cref="ITraceViewerService"/> + <see cref="DbcService"/> +
/// <see cref="TraceChartViewModel"/> for the Trace Viewer window.
/// v3.2.0 MINOR: backed by <see cref="ITraceSessionRegistry"/> (multi-trace
/// overlay) instead of a single <see cref="ITraceViewerService"/>. The
/// single-trace workflow (1 source) is a degenerate case of the registry —
/// <see cref="Sources"/>.Count == 1 — and behaves identically to v3.0/3.1.x.
/// <para>
/// v3.3.0 MINOR: sync playback across N traces. Playback commands
/// (<see cref="PlayCommand"/>, <see cref="PauseCommand"/>, <see cref="StopCommand"/>,
/// <see cref="SeekToCommand"/>) iterate the per-source services in
/// <see cref="_allServices"/>; proportional seek math lands in Task 2.
/// </para>
/// <para>
/// <b>Cursor propagation (single-trace mode):</b> identical to v3.0 —
/// the master source's <see cref="ITraceViewerService.FrameEmitted"/> fires
/// on the timeline's timer thread; we Post the cursor advance to the captured
/// <see cref="SynchronizationContext"/> for UI marshaling.
/// </para>
/// </summary>
public sealed partial class TraceViewerViewModel : ObservableObject, IDisposable, IChatToolContext
{
    // === Flow A methods moved to TraceViewerViewModel/SourceFlow.cs (W3 Task 3) ===
    // === Flow B methods moved to TraceViewerViewModel/TransportFlow.cs (W3 Task 4) ===
    // === Flow D methods moved to TraceViewerViewModel/WatchFlow.cs (W3 Task 5) ===
    // === Flow E methods moved to TraceViewerViewModel/SessionFlow.cs (W3 Task 6) ===
    private readonly ITraceSessionRegistry _registry;
    // v3.x (会话状态剥离 Task 3): session 级状态（watch 列表 / 分组 / master /
    // 全局过滤）的唯一归属。VM 保留同名属性转发，Dispose 反注册其 INPC 订阅。
    private readonly ITraceSessionService _session;
    private readonly DbcService _dbcService;
    private readonly ILogger<TraceViewerViewModel> _logger;
    private readonly TraceSessionLibrary _sessionLibrary;
    private readonly IFileDialogService? _fileDialog;
    private readonly IAscContentHasher _hasher;
    private readonly IAscLocator _locator;
    private readonly TraceSessionSnapshotBuilder _builder;
    // ChatSettingsFlow: multi-vendor credential store. Read/Set against
    // arbitrary PeakCan/{provider}/{alias} keys.
    private readonly ICredentialStore? _credentialStore;
    // v3.62.0 MINOR: progressive chart fill engine (background decode + incremental render)
    private readonly ChartFillEngine _fillEngine = new();
    private readonly Dictionary<string, FillRequest> _activeFillRequests = new();
    // v3.62.0: View-owned WpfPlot.Plot references, keyed by signalKey. VM adds anchor lines here.
    private readonly Dictionary<string, Plot> _activePlots = new();

    /// <summary>v3.62.0 MINOR: lookup active fill request for View to wire RefreshCallback</summary>
    public bool TryGetActiveFillRequest(string signalKey, out FillRequest request) =>
        _activeFillRequests.TryGetValue(signalKey, out request);

    /// <summary>v3.62.0 MINOR: View registers its WpfPlot.Plot so VM can add anchor lines.</summary>
    public void RegisterPlot(string signalKey, Plot plot)
    {
        _activePlots[signalKey] = plot;
    }

    /// <summary>v3.62.0 MINOR: unregister when series is removed.</summary>
    public void UnregisterPlot(string signalKey)
    {
        _activePlots.Remove(signalKey);
        _activeFillRequests.Remove(signalKey);
    }

    // Mirrors ReplayViewModel: FrameEmitted fires on the timeline's
    // timer thread. Captured at construction; null in test fixtures
    // without an STA SynchronizationContext (direct set is safe there).
    private readonly SynchronizationContext? _syncContext;
    private ITraceViewerService? _masterService;   // current master source's service (rebound on SourcesChanged)
    // v3.3.0 MINOR: registry of all N per-source services, keyed by SourceId.
    // Rebuilt on SourcesChanged. Play/Pause/Stop/Seek iterate this dict.
    private readonly Dictionary<string, ITraceViewerService> _allServices =
        new(StringComparer.Ordinal);
    private bool _disposed;

    [ObservableProperty]
    private string _loadedTracePath = "";

    [ObservableProperty]
    private string _loadedDbcPath = "";

    [ObservableProperty]
    private double _scrubberValue;

    [ObservableProperty]
    private double _totalDuration;

    // v3.x (会话状态剥离 Task 3): MasterSourceId 转发到 ITraceSessionService。
    // service 是 session 级状态唯一归属；VM 保留同名属性，INPC 由
    // OnSessionPropertyChanged 透传（去掉 [ObservableProperty] + 私有字段）。
    public string MasterSourceId
    {
        get => _session.MasterSourceId ?? "";
        set => _session.MasterSourceId = value;
    }

    // v3.3.0 MINOR: global loop toggle; propagates to master only (non-masters
    // use Loop=false — see OnRegistrySourcesChanged + master PlaybackEnded hook).
    [ObservableProperty]
    private bool _loop = false;

    // v3.3.0 MINOR: global speed multiplier; propagated to every service.
    [ObservableProperty]
    private double _speed = 1.0;

    // v3.4.2 PATCH: comma-separated CAN ID allow-list (decimal or 0x-hex,
    // case-insensitive). Empty = no filter. Parsed in RebuildSignalsAsync
    // and applied to both the global frame bucketing loop and the per-source
    // chart-series loop.
    // v3.x (会话状态剥离 Task 3): CanIdFilter 转发到 ITraceSessionService 的
    // GlobalCanIdFilter（去掉 [ObservableProperty]，INPC 由 service 经
    // OnSessionPropertyChanged 透传）。setter 保留同步重建 hook（原
    // [ObservableProperty] 的 OnCanIdFilterChanged → RebuildSignalsCore）——
    // 否则过滤器变更不会刷新各 watch 行的 FrameCount。
    public string CanIdFilter
    {
        get => _session.GlobalCanIdFilter;
        set
        {
            if (_session.GlobalCanIdFilter == value) return;
            _session.GlobalCanIdFilter = value;
            OnCanIdFilterChanged(value);
        }
    }

    // v3.9.1 PATCH Bug #2: IsLoading + ErrorMessage + StatusMessage.
    // IsLoading gates AddTraceCommand CanExecute (mirrors
    // ReplayViewModel.IsLoaded's 5-command gate at lines 101-112) so the
    // toolbar "Add trace…" button greys out during a load. ErrorMessage is
    // XAML-bound to a red TextBlock — parse failures surface as visible UI
    // feedback instead of a MessageBox. StatusMessage is XAML-bound to a
    // gray status bar showing the load lifecycle ("Loading foo.asc…" /
    // "Loaded foo.asc" / "Load failed" / "Load cancelled").
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddTraceCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveTraceCommand))]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string _statusMessage = "Status: ready";

    public ObservableCollection<TraceSignalRow> Signals { get; } = new();

    /// <summary>v3.15.0 MINOR: watch list (default empty; user adds
    /// explicitly via + Add to watch…). Replaces v3.14.3's
    /// "DBC 全列" `Signals` collection conceptually but keeps the
    /// legacy collection for back-compat until the v3.14.3 tests
    /// are migrated. New XAML binds to <see cref="WatchedSignals"/>
    /// instead.</summary>
    // v3.x (会话状态剥离 Task 3): get-only 转发到 service。ObservableCollection
    // 引用不变，内容变更由集合自身通知（VM 的 CollectionChanged 订阅照常工作）。
    public ObservableCollection<WatchedSignalRow> WatchedSignals => _session.WatchedSignals;

    // v3.x (会话状态剥离 Task 3): 信号分组同样转发到 service（原本在
    // ChatToolContextFlow.cs 声明，现与 WatchedSignals 一起归 service 所有）。
    public ObservableCollection<WatchedSignalGroup> SignalGroups => _session.SignalGroups;

    public TraceChartViewModel ChartViewModel { get; } = new();

    /// <summary>v3.2.0 MINOR: read-through to the registry. XAML binds the
    /// legend strip against this property (one entry per loaded source).</summary>
    public IReadOnlyList<TraceSource> Sources => _registry.Sources;

    // v3.15.0 MINOR: filename-only display of LoadedDbcPath for the
    // toolbar TextBlock. Full path is in the tooltip. Empty when no
    // DBC is loaded (B1 fix).
    public string LoadedDbcPathDisplay
        => string.IsNullOrEmpty(LoadedDbcPath)
            ? ""
            : System.IO.Path.GetFileName(LoadedDbcPath);

    /// <summary>v3.16.0 MINOR: return the current DBC for the
    /// <c>DbcTreePickerWindow</c> to walk, or null if no DBC is
    /// loaded (in which case the picker would be empty anyway).
    /// </summary>
    public DbcDocument? GetDbcForPicker() => _dbcService.Current;

    public TraceViewerViewModel(
        ITraceSessionService session,
        ITraceSessionRegistry registry,
        DbcService dbcService,
        ILogger<TraceViewerViewModel> logger,
        TraceSessionLibrary sessionLibrary,
        IFileDialogService? fileDialog = null,
        IAscContentHasher? hasher = null,
        IAscLocator? locator = null,
        TraceSessionSnapshotBuilder? builder = null,
        IChatProvider? chatProvider = null,
        IEnumerable<IChatTool>? chatTools = null,
        ICredentialStore? credentialStore = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _dbcService = dbcService ?? throw new ArgumentNullException(nameof(dbcService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sessionLibrary = sessionLibrary ?? throw new ArgumentNullException(nameof(sessionLibrary));
        _fileDialog = fileDialog;
        // v3.6.4 PATCH: defaults to a no-op hasher + locator so the
        // legacy ctor signature (without these args) keeps compiling
        // and the existing test suite is undisturbed. Tests that DO
        // exercise hash-based relocation inject real or fake instances.
        _hasher = hasher ?? NullAscContentHasher.Instance;
        _locator = locator ?? NullAscLocator.Instance;
        // v3.11.0 MINOR T2 (H7): default to a builder wrapping the same
        // hasher so existing test ctor calls (no builder arg) keep
        // compiling. Production DI wires a singleton builder; the
        // default keeps unit-test hermeticity — no DI container required.
        _builder = builder ?? new TraceSessionSnapshotBuilder(_hasher);
        // ChatSettingsFlow: 多厂商 Key 管理。nullable 保持旧测试构造签名兼容。
        // 生产 DI 传真实实例；测试中 null 时 ChatSettingsFlow 功能不可用（不崩溃）。
        _credentialStore = credentialStore ?? null!;
        // AI Chat (Step 4-5): nullable so legacy test ctor calls keep compiling;
        // production DI passes a real IChatProvider + the 6 IChatTool instances.
        _chatProvider = chatProvider;
        _chatTools = (chatTools ?? Enumerable.Empty<IChatTool>()).ToList();
        // v3.62.0 MINOR: wire plot resolver for axis sync (View owns the actual Plot objects)
        ChartViewModel.PlotResolver = key => _activePlots.TryGetValue(key, out var p) ? p : null;
        _syncContext = SynchronizationContext.Current;
        _registry.SourcesChanged += OnRegistrySourcesChanged;
        // v3.13.2 PATCH F5: subscribe to DbcService.DbcLoaded so the Trace
        // Viewer auto-rebuilds Signals + chart subplots when a DBC is loaded
        // via the DbcView tab. The xmldoc above (line 388) historically
        // documented this as "_dbcService.PropertyChanged" but DbcService
        // does not implement INotifyPropertyChanged — it exposes the typed
        // DbcLoaded event. The handler is cancelled in Dispose() per
        // v3.14.0 MINOR A4; DbcService is a DI singleton so without that
        // cancellation the subscription would pin the VM for the app
        // lifetime (the singleton holds a strong reference to the handler
        // closure, which transitively pins the VM and its Frames /
        // Signals / ChartViewModel state).
        _dbcService.DbcLoaded += OnDbcLoaded;
        // Initial pull — captures any pre-loaded sources (none in normal startup).
        // OnRegistrySourcesChanged populates _allServices and rebinds master;
        // a bare RebindMasterFromRegistry would leave _allServices empty.
        OnRegistrySourcesChanged();
        // v3.49.0 MINOR Q1: hook WatchedSignals collection mutation so the
        // Sampling Table right-edge panel stays in sync.
        WatchedSignals.CollectionChanged += OnWatchedSignalsCollectionChangedForSamplingTable;
        // v3.50.0 MINOR Q1 redesign: pre-resolve DbcSignal reference per
        // watched row so RefreshAtAnchor (T2) can decode raw bits at the
        // anchor timestamp without an extra DBC scan on the UI thread.
        WatchedSignals.CollectionChanged += OnWatchedSignalsCollectionChangedForSignalCache;
        // v3.x (会话状态剥离 Task 3): 订阅 service 的 INPC，把 MasterSourceId /
        // GlobalCanIdFilter 变更透传到 VM 同名属性（具名 handler，Dispose 反注册，
        // 避免 singleton service 强引用 VM）。
        _session.PropertyChanged += OnSessionPropertyChanged;
        // v3.x (会话状态剥离 Task 5 final, Important #2): 订阅 SessionRestored——
        // OpenSessionAsync 恢复完 watch 列表/分组后触发，VM 据此补刷 FrameCount 与
        // 锚点（否则开着的窗口看到恢复前/空的列表）。具名 handler，Dispose 反注册。
        _session.SessionRestored += OnSessionRestored;
    }

    private readonly Dictionary<string, PeakCan.HIL.Core.Dbc.Signal?> _signalByKey = new(StringComparer.Ordinal);

    private void OnWatchedSignalsCollectionChangedForSignalCache(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is null) return;
        var dbc = _dbcService.Current;
        if (dbc is null) return;
        foreach (WatchedSignalRow row in e.NewItems)
        {
            var key = row.SignalKey;
            if (_signalByKey.ContainsKey(key)) continue;
            // Inline message lookup by (id + name) — DbcDocument has no
            // FindSignal helper, so walk Messages once per add.
            var msg = dbc.MessagesById.Values.FirstOrDefault(m => m.Name == row.MessageName);
            var sig = msg?.Signals.FirstOrDefault(s => s.Name == row.SignalName);
            _signalByKey[key] = sig;
            // v3.50.5 PATCH: bind Signal + Dbc on the row so the
            // .Text computed properties can resolve VAL_ table entries.
            // Dbc setter triggers PropertyChanged for LatestText/BlueText/DeltaText.
            row.Signal = sig;
            row.Dbc = dbc;
        }
    }

    // v3.49.0 MINOR Q1: 替代原匿名 lambda 的具名 handler——WatchedSignals 变更时
    // 刷新右侧 Sampling Table。具名以便 Dispose 反注册（匿名 lambda 无法 -=）。
    private void OnWatchedSignalsCollectionChangedForSamplingTable(object? sender, NotifyCollectionChangedEventArgs e)
        => RefreshSamplingTable();

    // v3.x (会话状态剥离 Task 3): service 的 MasterSourceId / GlobalCanIdFilter
    // 变更透传到 VM 同名属性（XAML 绑定目标仍是 VM 属性）。重建 hook 由
    // CanIdFilter setter 直接触发（见上），此处只转发 INPC，避免生产环境
    // service 同步抛事件 + setter 再重建导致的双重重建。
    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ITraceSessionService.MasterSourceId))
            OnPropertyChanged(nameof(MasterSourceId));
        else if (e.PropertyName == nameof(ITraceSessionService.GlobalCanIdFilter))
            OnPropertyChanged(nameof(CanIdFilter));
    }

    /// <summary>
    /// v3.x (会话状态剥离 Task 3): 原 [ObservableProperty] _canIdFilter 生成的
    /// 分部方法 hook 定义。属性转发到 service 后改由 <see cref="CanIdFilter"/>
    /// setter 调用（不再由生成的 setter 触发），实现仍在 SignalFlow.cs
    /// （RebuildSignalsCore）。
    /// </summary>
    partial void OnCanIdFilterChanged(string value);

    /// <summary>v3.2.0 MINOR: XAML binding source for the legend strip's
    /// <c>Visibility</c>. True when at least one trace is loaded.</summary>
    public bool HasSources => Sources.Count > 0;


    // Flow C moved to TraceViewerViewModel/SignalFlow.cs (W3 Task 1)

    // === Flow G methods moved to TraceViewerViewModel/PlaybackFlow.cs (W20 Task 1) ===
    /// <summary>
    /// Rebuild the left-side <see cref="Signals"/> collection from the
    /// currently loaded trace + (optional) DBC. v3.2.0 MINOR: walks
    /// <see cref="ITraceSessionRegistry.GetFrames"/> per source so multi-trace
    /// overlays see all frames across all loaded sources.
    /// </summary>
    // v3.13.0 PATCH F3: changed from `private` to `internal` so the test
    // assembly can drive it directly. LoadDbcAsync was deleted (the
    // "Load DBC…" toolbar button was dead — no UI feedback), but the
    // tests still need a way to trigger a rebuild against a pre-loaded
    // DBC (set via DbcService.SetCurrentForTests). Visible to
    // PeakCan.Host.App.Tests via the existing InternalsVisibleTo attr.
    internal async Task RebuildSignalsAsync()
    {
        RebuildSignalsCore();
        await Task.CompletedTask;
    }

    // === Flow C methods moved to TraceViewerViewModel/SignalFlow.cs (W3 Task 1) ===

    /// <summary>
    /// v3.14.3 PATCH: stub. Chart series are no longer auto-built at
    /// load time — the user opts in per-signal via the Plot checkbox
    /// in the signal table, which calls <see cref="TogglePlot"/> →
    /// <see cref="PlotSignalFromTableRow"/> → <see cref="BuildOneChartSeriesForSource"/>.
    /// Kept as a no-op stub for legacy callers (the original
    /// implementation eagerly allocated 316 placeholder PlotModels
    /// per ASC load).
    /// </summary>
    [System.Obsolete("v3.14.3 PATCH: chart series are now user-opt-in via TogglePlot; BuildChartSeries is a no-op stub.", false)]
    private void BuildChartSeries(
        IReadOnlySet<uint>? globalAllowed,
        DbcDocument dbc)
    {
        // No-op. Chart rows are created lazily on user opt-in.
    }

    // === Flow H methods moved to TraceViewerViewModel/ChartSeriesFlow.cs (W20 Task 2) ===

    /// <summary>
    /// Unsubscribe from the registry + master service and stop playback.
    /// Safe to call multiple times — <c>_disposed</c> guards re-entry.
    /// <para>
    /// v3.14.0 MINOR A4: cancel the v3.13.2 PATCH F5 DbcLoaded
    /// subscription. The ctor xmldoc at line 174-180 previously
    /// defended "no unsubscribe because DbcService is a DI singleton"
    /// — backwards reasoning. The singleton holds a strong reference
    /// to the handler closure, which pins the VM (and its Frames /
    /// Signals / ChartViewModel state) for the app lifetime. Each
    /// Trace Viewer close+reopen without this unsubscribe leaks a
    /// full VM.
    /// </para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // v3.14.0 MINOR A4: cancel the DbcLoaded subscription. Matches
        // the += in the ctor.
        _dbcService.DbcLoaded -= OnDbcLoaded;
        // v3.x (会话状态剥离 Task 3): VM 变 transient，必须反注册对 singleton
        // 对象（_session / WatchedSignals 集合）的订阅，否则 singleton 强引用
        // VM handler 导致 VM 泄漏。
        WatchedSignals.CollectionChanged -= OnWatchedSignalsCollectionChangedForSamplingTable;
        WatchedSignals.CollectionChanged -= OnWatchedSignalsCollectionChangedForSignalCache;
        _session.PropertyChanged -= OnSessionPropertyChanged;
        _session.SessionRestored -= OnSessionRestored;
        DetachAllServiceHandlers();
        // v3.4.3 PATCH (Task 3 review round 1, Important #1): VM 变 transient 后
        // 必须同时反注册对 registry source 的 per-source INPC 订阅。OnRegistrySourcesChanged
        // 会对每个 TraceSource 订阅 src.PropertyChanged += OnAnySourcePropertyChanged；
        // TraceSource 由 singleton registry 持有——不反注册则 singleton 强引用已释放
        // VM 的 handler，每次关窗重开泄漏一个 VM（正是本类 doc comment 警告的失败模式）。
        DetachAllSourcePropertyHandlers();
        _registry.SourcesChanged -= OnRegistrySourcesChanged;
        // v3.62.0 BUG-FIX: 取消并释放聊天 CancellationTokenSource, 中止 in-flight HTTP 请求
        _chatCts?.Cancel();
        _chatCts?.Dispose();
        _chatCts = null;
        GC.SuppressFinalize(this);
    }
}


// === Null helper classes moved to Helpers/NullAscServices.cs (W20 Task 3) ===
