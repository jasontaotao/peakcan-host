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
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;
using PeakCan.Host.Core.Replay;
using System.Collections.Specialized;
using PeakCan.Host.Core.Services;

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
public sealed partial class TraceViewerViewModel : ObservableObject, IDisposable
{
    // === Flow A methods moved to TraceViewerViewModel/SourceFlow.cs (W3 Task 3) ===
    // === Flow B methods moved to TraceViewerViewModel/TransportFlow.cs (W3 Task 4) ===
    // === Flow D methods moved to TraceViewerViewModel/WatchFlow.cs (W3 Task 5) ===
    // === Flow E methods moved to TraceViewerViewModel/SessionFlow.cs (W3 Task 6) ===
    private readonly ITraceSessionRegistry _registry;
    private readonly DbcService _dbcService;
    private readonly ILogger<TraceViewerViewModel> _logger;
    // v3.5.0 MINOR: .tmtrace bundle save/load. Persists the multi-trace
    // session (sources + per-source filter + global filter + playback
    // cursor + master + loop + speed + DBC path) as a single JSON
    // document via atomic tmp+rename.
    private readonly TraceSessionLibrary _sessionLibrary;
    // v3.5.0 MINOR: file-dialog abstraction so the Save/Open commands
    // can be unit-tested with a fake (no WPF Application needed).
    // Production DI wires the WPF impl; tests inject a fake that
    // returns a canned path or simulates cancellation.
    private readonly IFileDialogService? _fileDialog;
    // v3.6.4 PATCH: hash-based .asc relocation. Optional — both fields
    // default to no-op fakes in the legacy single-arg test ctor (the
    // existing test pattern that doesn't care about hashing). Production
    // DI wires the real SHA-256 hasher + file-system locator.
    private readonly IAscContentHasher _hasher;
    private readonly IAscLocator _locator;
    // v3.11.0 MINOR T2 (H7): shared BuildSnapshot logic. Trace +
    // Replay VMs delegate the scalar envelope to this helper; the
    // Trace VM still iterates Sources itself (N sources, per-source
    // color + stroke style + filter) but uses the builder for the
    // version / schema / savedAt / appVersion envelope.
    private readonly TraceSessionSnapshotBuilder _builder;
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

    [ObservableProperty]
    private string _masterSourceId = "";

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
    [ObservableProperty]
    private string _canIdFilter = "";

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
    public ObservableCollection<WatchedSignalRow> WatchedSignals { get; } = new();

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
        ITraceSessionRegistry registry,
        DbcService dbcService,
        ILogger<TraceViewerViewModel> logger,
        TraceSessionLibrary sessionLibrary,
        IFileDialogService? fileDialog = null,
        IAscContentHasher? hasher = null,
        IAscLocator? locator = null,
        TraceSessionSnapshotBuilder? builder = null)
    {
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
        WatchedSignals.CollectionChanged += (_, _) => RefreshSamplingTable();
        // v3.50.0 MINOR Q1 redesign: pre-resolve DbcSignal reference per
        // watched row so RefreshAtAnchor (T2) can decode raw bits at the
        // anchor timestamp without an extra DBC scan on the UI thread.
        WatchedSignals.CollectionChanged += OnWatchedSignalsCollectionChangedForSignalCache;
    }

    private readonly Dictionary<string, PeakCan.Host.Core.Dbc.Signal?> _signalByKey = new(StringComparer.Ordinal);

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
            row.Signal = sig;
        }
    }



    /// <summary>
    /// v3.13.0 PATCH F2: clear all mutable UI state when the Trace Viewer
    /// window closes. Prevents the "close + reopen shows stale state / NRE"
    /// bug because the singleton VM (shared with AppShell OpenSession/
    /// SaveSession menu commands) accumulates state across opens. Called
    /// from AppShellViewModel.ShowTraceViewer's Closed handler.
    /// <para>
    /// Strategy: snapshot the current sourceIds, then unload each via the
    /// registry (the only contract surface that drops sources + cascades
    /// INPC). <see cref="Signals"/> + <see cref="ChartViewModel.Series"/>
    /// are dropped in turn. Per-source state lives on each TraceSource
    /// and is reclaimed when the source is unloaded.
    /// </para>
    /// <para>
    /// Does NOT clear <see cref="LoadedDbcPath"/> — that's restored from the
    /// loaded .tmtrace bundle on next OpenSession and is not "open-window
    /// state" in the same sense. Does NOT unsubscribe from
    /// <c>_registry.SourcesChanged</c> / <c>_dbcService.PropertyChanged</c>
    /// — those are VM-lifetime subscriptions, not window-lifetime.
    /// </para>
    /// </summary>
    public void Reset()
    {
        // Snapshot sourceIds before unloading — _registry.Sources shrinks
        // as we unload, so iterating the live list would mutate-while-
        // iterate. The registry allows this safely, but copying is clearer.
        var sourceIds = _registry.Sources.Select(s => s.SourceId).ToList();
        foreach (var sourceId in sourceIds)
        {
            // UnloadAsync is fire-and-forget by contract (returns a Task
            // but we have no continuation). Capturing the task and not
            // awaiting keeps Reset() synchronous, matching the WPF Closed
            // handler's fire-and-forget nature.
            _ = _registry.UnloadAsync(sourceId);
        }
        Signals.Clear();
        ChartViewModel.Series.Clear();
        ScrubberValue = 0;
        Speed = 1.0;
        Loop = false;
        MasterSourceId = "";
        CanIdFilter = "";
        ErrorMessage = null;
        StatusMessage = "Status: ready";
        IsLoading = false;

        // v3.50.1 PATCH: singleton VM is reused across Trace Viewer
        // close+reopen cycles (ViewSwitcher caches the window, AppShell
        // hands the same VM back via DI). The pre-v3.50.1 Reset only
        // cleared the v3.0 MINOR-era fields above; v3.15.0+ collections
        // (WatchedSignals), v3.50 caches (_signalByKey + anchor state),
        // and v3.49 right-edge panel state (SamplingRows) survived
        // close+reopen — leaving the watch list visually empty
        // (DataGrid bound to a populated ObservableCollection with no
        // INPC diff signal after the window's visual tree is rebuilt,
        // the Generator doesn't re-materialize rows for the cached VM)
        // or with stale DbcSignal refs pointing at the previous DBC.
        // Clear every mutable UI collection + cache before the window
        // teardown returns to the caller.
        WatchedSignals.Clear();
        _signalByKey.Clear();
        _anchorTimestampSeconds = double.NaN;
        OnPropertyChanged(nameof(IsGreenLineAnchorActive));
        // UpdateAllGreenLines is idempotent and removes every existing
        // green-anchor LineAnnotation; with _anchorTimestampSeconds =
        // NaN the IsGreenLineAnchorActive gate skips adding a new one,
        // so this is effectively a "drop all green lines" pass.
        UpdateAllGreenLines();
        SamplingRows.Clear();
    }

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
        DetachAllServiceHandlers();
        _registry.SourcesChanged -= OnRegistrySourcesChanged;
        GC.SuppressFinalize(this);
    }
}


// === Null helper classes moved to Helpers/NullAscServices.cs (W20 Task 3) ===
