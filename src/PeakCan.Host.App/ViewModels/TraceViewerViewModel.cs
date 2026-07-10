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
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;
using PeakCan.Host.Core.Replay;
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
    }

    /// <summary>v3.2.0 MINOR: XAML binding source for the legend strip's
    /// <c>Visibility</c>. True when at least one trace is loaded.</summary>
    public bool HasSources => Sources.Count > 0;

    [RelayCommand]
    public void Play()
    {
        // v3.16.7 DIAG: log PlayCommand entry with full state
        _logger.LogInformation("TraceViewerViewModel.Play() ENTER: _allServices.Count={Count} masterId={Master} HasSources={Has}",
            _allServices.Count, MasterSourceId, HasSources);
        foreach (var (id, svc) in _allServices)
        {
            _logger.LogInformation("  calling svc.Play() for SourceId={Id} TotalDuration={Dur}",
                id, svc.TotalDuration);
            svc.Play();
        }
    }

    [RelayCommand]
    public void Pause()
    {
        foreach (var svc in _allServices.Values)
            svc.Pause();
    }

    [RelayCommand]
    public void Stop()
    {
        foreach (var svc in _allServices.Values)
            svc.Stop();
        ScrubberValue = 0;
    }

    [RelayCommand]
    public void SeekTo(double t)
    {
        // v3.8.6 PATCH H1: clamp is in SeekAllToProportionalTime
        // (defense-in-depth with v3.8.4 L1 Replay-tab pattern). The
        // ScrubberValue setter is left raw so the slider thumb reflects
        // what the user actually typed/programmatic; the timeline-side
        // clamp rejects out-of-range values before they reach the
        // service. Doing the seek here too would double-call every
        // service — single source of truth is the scrubber change handler.
        ScrubberValue = t;
    }

    partial void OnScrubberValueChanged(double value)
    {
        // v3.16.9.2 PATCH: reverse-trigger guard. Playback's
        // OnAnyFrameEmitted writes ScrubberValue = t every frame;
        // without this guard, the setter calls SeekAllToProportionalTime
        // → master.Seek(t) → ReplayTimeline.Seek(t) resets
        // _playStartTimestamp = t (line 181). Effect: PlayedTimestamp
        // = t + elapsed_after_seek where t = frame.ts of the emit, so
        // every tick "advances" by elapsed but t also advances by
        // ~0.1s per emit. Net: cursor snaps to trace-end on the first
        // emit, then continues at trace-end in a fast-forward loop
        // (5000 frames in 0.013s observed in production). User
        // symptom: "progress bar jumps straight to end" — exactly the
        // v3.16.3 PATCH-introduced regression.
        //
        // Guard: when master is actively playing, the ScrubberValue
        // setter writes are writebacks from FrameEmitted, not user
        // input. Skip the seek in that case. User drag is unaffected
        // (master is not playing during drag because Pause would be
        // hit first, or IsPlaying=false).
        if (_masterService is null) return;
        if (_masterService.State == ReplayState.Playing) return;
        if (TotalDuration > 0)
            SeekAllToProportionalTime(value);
    }

    partial void OnLoopChanged(bool value)
    {
        PropagateLoopToAllServices();
    }

    partial void OnSpeedChanged(double value)
    {
        PropagateSpeedToAllServices();
    }

    // Flow C moved to TraceViewerViewModel/SignalFlow.cs (W3 Task 1)

    // v3.4.2 PATCH: XAML "Clear" button binding. Empty string → parser
    // returns null → unfiltered rebuild.
    [RelayCommand]
    private void ClearCanIdFilter() => CanIdFilter = "";

    /// <summary>
    /// v3.5.0 MINOR: save the current Trace Viewer session to a
    /// <c>.tmtrace</c> bundle. <paramref name="path"/> is supplied by
    /// the View's <c>SaveFileDialog</c>; the command itself does NOT
    /// pop a dialog (testability — the View handles the file dialog
    /// to keep WPF dependency out of the VM).
    /// </summary>
    [RelayCommand]
    public async Task SaveSessionAsync(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        var snapshot = BuildSnapshot();
        await Task.Run(() => _sessionLibrary.Save(snapshot, path)).ConfigureAwait(true);
    }

    /// <summary>
    /// v3.5.0 MINOR: load a Trace Viewer session from a <c>.tmtrace</c>
    /// bundle. The caller (View) handles the open-file dialog and the
    /// missing-ascs MessageBox UX — the VM returns the list of paths
    /// that could not be resolved (e.g. an .asc that was moved/deleted
    /// since the bundle was saved) so the View can surface them.
    /// Restores playback to a paused/stopped cursor — never auto-resumes.
    /// </summary>
    /// <returns>List of source .asc paths that did NOT resolve on load.
    /// Empty when the bundle had no sources or when every source
    /// resolved cleanly.</returns>
    public async Task<IReadOnlyList<string>> OpenSessionAsync(string? path)
    {
        if (string.IsNullOrEmpty(path)) return Array.Empty<string>();
        var dto = await Task.Run(() => _sessionLibrary.Load(path)).ConfigureAwait(true);
        if (dto is null) return Array.Empty<string>();
        return await ApplySnapshotAsync(dto).ConfigureAwait(true);
    }

    /// <summary>
    /// v3.5.0 MINOR: collect the current session state into a
    /// <see cref="TraceSessionBundleDto"/>. Pure — no I/O, no side
    /// effects. Path-reference only for .asc recordings; playback state
    /// is captured verbatim (master, loop, speed, scrubber) and the
    /// DBC path is recorded (the DBC service is not re-loaded — the
    /// caller will reload it as part of <see cref="ApplySnapshotAsync"/>
    /// once the sources are loaded).
    /// <para>
    /// v3.6.0 MINOR T2: access changed from <c>private</c> to
    /// <c>public</c> so <see cref="TraceSessionAutoSaver"/> can snapshot
    /// the live VM during <c>App.OnExit</c>. Behavior unchanged.
    /// </para>
    /// <para>
    /// v3.11.0 MINOR T2 (H7): the scalar envelope (Version / Schema /
    /// SavedAt / AppVersion / DbcPath / GlobalCanIdFilter) now lives in
    /// <see cref="TraceSessionSnapshotBuilder"/>. This method is the
    /// thin sync shim over <see cref="BuildSnapshotAsync"/>; new
    /// callers should prefer the async form. Per-source iteration +
    /// per-source hashing still lives here (N sources + per-source
    /// color / stroke style / filter).
    /// </para>
    /// </summary>
    public TraceSessionBundleDto BuildSnapshot() =>
        BuildSnapshotAsync().GetAwaiter().GetResult();

    /// <summary>
    /// v3.11.0 MINOR T2 (H7): async BuildSnapshot entry point. Same
    /// shape as <see cref="BuildSnapshot"/> but awaits the shared
    /// builder's scalar envelope assembly. CT propagates to each
    /// per-source hasher call.
    /// </summary>
    public async Task<TraceSessionBundleDto> BuildSnapshotAsync(CancellationToken ct = default)
    {
        var scaffold = new TraceSessionSnapshotBuilder.Scaffold(
            LoadedFilePath: null,    // Trace iterates N sources — the builder's single-source path is unused
            CurrentTimestamp: ScrubberValue,
            Speed: Speed,
            Loop: Loop,
            StartTimestamp: 0.0,
            EndTimestamp: 0.0,
            CanIdFilterText: CanIdFilter ?? "",
            DbcPath: LoadedDbcPath ?? "");
        var dto = await _builder.BuildAsync(scaffold, ct).ConfigureAwait(true);

        // Per-source assembly stays on the VM: N sources, per-source
        // color + stroke style + filter, plus N per-source hashes
        // (the builder's single-source pre-population is overwritten).
        dto.Sources = new List<BundleSourceDto>(Sources.Count);
        foreach (var src in Sources)
        {
            // v3.6.4 PATCH: populate contentHash when the source's
            // .asc still exists on disk so the bundle can later be
            // relocated via the SHA-256 lookup.
            var hash = "";
            if (!string.IsNullOrEmpty(src.Path) && File.Exists(src.Path))
            {
                try
                {
                    hash = await _hasher.ComputeAsync(src.Path, ct).ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    // Hashing failed (locked file / ACL). Skip — the
                    // bundle still saves with contentHash="" and the
                    // path-only resolution covers it on reload.
                    LogHashFailed(_logger, ex, src.Path);
                    hash = "";
                }
            }
            dto.Sources.Add(new BundleSourceDto
            {
                SourceId = src.SourceId,
                DisplayName = src.DisplayName,
                Path = src.Path,
                ColorA = src.Color.A,
                ColorR = src.Color.R,
                ColorG = src.Color.G,
                ColorB = src.Color.B,
                StrokeStyle = src.StrokeStyle.ToString(),
                CanIdFilter = src.CanIdFilter ?? "",
                ContentHash = hash,
            });
        }
        dto.Playback = new BundlePlaybackDto
        {
            MasterSourceId = MasterSourceId ?? "",
            Loop = Loop,
            Speed = Speed,
            ScrubberValue = ScrubberValue,
            StartTimestamp = null,
            EndTimestamp = null,
        };
        dto.Viewports = new List<BundleViewportDto>(ChartViewModel.CaptureViewports());
        return dto;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "BuildSnapshot: hashing failed for {Path}; bundle saved without contentHash")]
    private static partial void LogHashFailed(ILogger logger, Exception ex, string path);

    /// <summary>
    /// v3.5.0 MINOR: restore a saved session. Loads each .asc via the
    /// registry, applies playback state (always to a paused cursor —
    /// never auto-resumes), then restores chart viewports AFTER
    /// <see cref="RebuildSignalsCore"/> populates the Series collection
    /// (otherwise the per-axis writes would land on stale or empty
    /// PlotModels and <see cref="TraceChartViewModel.SyncYAxes"/> would
    /// overwrite them).
    /// </summary>
    private async Task<IReadOnlyList<string>> ApplySnapshotAsync(TraceSessionBundleDto dto)
    {
        var missing = new List<string>();
        // Unload any currently-loaded sources so the session is exactly
        // what the bundle describes. UnloadAsync is async but the inner
        // work is synchronous; we await to keep ordering deterministic.
        foreach (var src in Sources.ToList())
        {
            await _registry.UnloadAsync(src.SourceId).ConfigureAwait(true);
        }
        // Map sourceId → DisplayName so we can re-stamp after load.
        var nameBySourceId = dto.Sources.ToDictionary(s => s.SourceId, s => s.DisplayName, StringComparer.Ordinal);
        var pathBySourceId = dto.Sources.ToDictionary(s => s.SourceId, s => s.Path, StringComparer.Ordinal);
        // 1. Reload the .asc files via the registry. Missing → recorded
        //    in the returned list; do NOT throw (user-friendly).
        foreach (var bs in dto.Sources)
        {
            // v3.6.4 PATCH: when the recorded path is missing AND the
            // bundle carries a contentHash, ask the locator for a
            // relocated copy before giving up. The relocated path is
            // used for the registry load; if the locator also fails,
            // we fall through to the existing missing-path reporting.
            var loadPath = bs.Path;
            if (!string.IsNullOrEmpty(bs.Path) &&
                !File.Exists(bs.Path) &&
                !string.IsNullOrEmpty(bs.ContentHash))
            {
                var relocated = await _locator.LocateAsync(bs.ContentHash).ConfigureAwait(true);
                if (!string.IsNullOrEmpty(relocated) && File.Exists(relocated))
                {
                    LogRelocated(_logger, bs.Path, relocated);
                    loadPath = relocated;
                }
            }
            try
            {
                var loaded = await _registry.LoadAsync(loadPath).ConfigureAwait(true);
                // v3.6.0 MINOR T1.B: restore DisplayName and color from
                // the bundle, replacing the v3.5.0 "path-reference only"
                // comment. The registry's LoadAsync stamps a default
                // DisplayName (filename) and palette color; both are
                // overwritten when the bundle supplies values. For
                // bundle entries where color was left at default ARGB =
                // (0,0,0,0), the property set is skipped so the
                // registry's palette color survives (forward-compat with
                // hand-edited v1 bundles that pre-date color capture).
                loaded.CanIdFilter = bs.CanIdFilter;
                var filenameOnly = Path.GetFileNameWithoutExtension(bs.Path);
                if (!string.IsNullOrEmpty(bs.DisplayName) &&
                    bs.DisplayName != filenameOnly)
                {
                    loaded.DisplayName = bs.DisplayName;
                }
                if (!(bs.ColorA == 0 && bs.ColorR == 0 &&
                      bs.ColorG == 0 && bs.ColorB == 0))
                {
                    loaded.Color = OxyColor.FromArgb(
                        bs.ColorA, bs.ColorR, bs.ColorG, bs.ColorB);
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                LogSourceMissing(_logger, bs.Path, ex);
                missing.Add(bs.Path);
            }
            catch (ReplayException ex)
            {
                LogSourceMissing(_logger, bs.Path, ex);
                missing.Add(bs.Path);
            }
        }
        // 2. Apply DBC path if present. Best-effort — missing DBC is
        //    acceptable (user can reload manually).
        if (!string.IsNullOrEmpty(dto.DbcPath) && File.Exists(dto.DbcPath))
        {
            try { await _dbcService.LoadAsync(dto.DbcPath).ConfigureAwait(true); }
            catch (FileNotFoundException) { /* bundle references a deleted DBC — acceptable */ }
            catch (Exception ex)
            {
                // v3.9.2 PATCH L1: was a bare catch{ } swallowing all failures.
                // Log the DBC load failure so the operator can diagnose a
                // malformed-vendor-DBC without losing visibility. StatusMessage
                // surfaces it on the toolbar; the source still loads (the
                // bundle is path-reference only, so a missing/bad DBC is
                // not fatal — the user can reload manually).
                // v3.13.0 PATCH F3: renamed helper (was LogBundleDbcLoadFailed).
                // LoadDbcAsync's deletion made the old name misleading; this
                // arm is now the only caller.
                LogBundleDbcLoadFailedInline(_logger, dto.DbcPath, ex);
                StatusMessage = $"DBC load failed: {ex.Message}";
            }
            LoadedDbcPath = dto.DbcPath;
        }
        else
        {
            LoadedDbcPath = dto.DbcPath ?? "";
        }
        // 3. Apply global filter + playback transport. Always to a
        //    paused cursor — never auto-resume on app restart.
        CanIdFilter = dto.GlobalCanIdFilter ?? "";
        Loop = dto.Playback?.Loop ?? false;
        Speed = dto.Playback?.Speed ?? 1.0;
        ScrubberValue = 0.0;
        if (dto.Playback is { } pb && !string.IsNullOrEmpty(pb.MasterSourceId))
        {
            // The new SourceId from registry.LoadAsync != bundle's pre-recorded
            // id. Map via display name — same alpha-order as the registry
            // adds them, and the bundle's order matches.
            var newMaster = Sources.FirstOrDefault(s =>
                string.Equals(s.DisplayName, nameBySourceId.GetValueOrDefault(pb.MasterSourceId, ""), StringComparison.Ordinal));
            if (newMaster is not null)
            {
                MasterSourceId = newMaster.SourceId;
                _masterService = _registry.GetService(newMaster.SourceId);
                TotalDuration = _masterService?.TotalDuration ?? 0.0;
                ChartViewModel.SetTotalDuration(TotalDuration);
                // Seek to saved scrubber position (paused).
                if (_masterService is not null && pb.ScrubberValue > 0)
                {
                    _masterService.Seek(pb.ScrubberValue);
                    ScrubberValue = pb.ScrubberValue;
                }
            }
        }
        // 4. Rebuild signals + chart with the new source set, then apply
        //    viewports AFTER SyncYAxes has run so the X-axis writes stick.
        RebuildSignalsCore();
        // v3.5.1 PATCH (review M2): explicit assignment removes the
        // implicit dependency on _registry.LoadAsync firing
        // OnRegistrySourcesChanged synchronously inside ApplySnapshotAsync.
        // If the registry were ever to dispatch SourcesChanged
        // asynchronously, the property would still be correct here.
        LoadedTracePath = Sources.Count > 0 ? Sources[0].Path : "";
        ChartViewModel.ApplyViewports(dto.Viewports);
        return missing;
    }

    private void PropagateLoopToAllServices()
    {
        foreach (var svc in _allServices.Values)
            svc.Loop = Loop;
    }

    private void PropagateSpeedToAllServices()
    {
        foreach (var svc in _allServices.Values)
            svc.SetSpeed(Speed);
    }



    /// <summary>
    /// v3.14.3 PATCH: opt-in/opt-out handler invoked from the DataGrid
    /// checkbox Click handler in <c>TraceViewerView.xaml.cs</c>.
    /// Decides whether to add or remove chart series based on the
    /// new <see cref="TraceSignalRow.IsPlotted"/> value (the binding
    /// updates it before this method fires, thanks to
    /// <c>UpdateSourceTrigger=PropertyChanged</c>).
    /// </summary>
    [RelayCommand]
    public void TogglePlot(WatchedSignalRow row)
    {
        if (row is null) throw new ArgumentNullException(nameof(row));
        if (row.IsPlotted)
            PlotSignalFromTableRow(row);
        else
            UnplotSignalFromTableRow(row);
    }

    /// <summary>
    /// v3.14.3 PATCH: explicit opt-in. Tests and programmatic callers
    /// use this directly (no binding lag concerns). Production XAML
    /// uses <see cref="TogglePlot(WatchedSignalRow)"/> which inspects
    /// the row's <see cref="WatchedSignalRow.IsPlotted"/> after the
    /// binding has updated it.
    /// </summary>
    public void SetPlotOptIn(WatchedSignalRow row, bool optIn)
    {
        if (row is null) throw new ArgumentNullException(nameof(row));
        if (optIn)
            PlotSignalFromTableRow(row);
        else
            UnplotSignalFromTableRow(row);
    }

    /// <summary>
    /// v3.14.3 PATCH back-compat: legacy overload accepting the old
    /// <see cref="TraceSignalRow"/> record. Wraps the call by
    /// forwarding via the row's INPC fields (SignalKey / SignalName /
    /// CanIdHex). New code should call the
    /// <see cref="SetPlotOptIn(WatchedSignalRow, bool)"/> overload
    /// directly. The wrapping builds a transient
    /// <see cref="WatchedSignalRow"/> from the legacy row's fields.
    /// </summary>
    public void SetPlotOptIn(TraceSignalRow row, bool optIn)
    {
        if (row is null) throw new ArgumentNullException(nameof(row));
        var transient = new WatchedSignalRow(
            canIdHex: row.CanIdHex,
            messageName: "",
            signalName: row.SignalName,
            unit: row.Unit);
        SetPlotOptIn(transient, optIn);
    }

    /// <summary>
    /// v3.15.0 MINOR: add a signal to the user's watch list. Invoked
    /// from the <c>+ Add to watch…</c> toolbar button (which opens a
    /// <c>DbcTreePickerWindow</c> for the user to pick a message +
    /// signal). Creates a new <see cref="WatchedSignalRow"/>,
    /// appends to <see cref="WatchedSignals"/>, and immediately plots
    /// the chart series for the watched source(s). Idempotent on
    /// duplicate (canId, signalName, sourceId) — silently no-ops if
    /// the row already exists.
    /// <para>
    /// Not decorated with <c>[RelayCommand]</c> because the toolkit's
    /// generator does not support 3-arg signatures. Callers (XAML
    /// code-behind, programmatic) invoke this method directly.
    /// </para>
    /// </summary>
    public void AddToWatch(uint canId, string signalName, string sourceId)
    {
        if (_dbcService.Current is null) return;
        var dbc = _dbcService.Current;

        // Lookup the message + signal in the DBC.
        var maskedId = canId & 0x7FFFFFFFu;
        var msg = dbc.Messages.FirstOrDefault(m => (m.Id & 0x7FFFFFFFu) == maskedId);
        if (msg is null) return;
        var sig = msg.Signals.FirstOrDefault(s => s.Name == signalName);
        if (sig is null) return;

        // Treat empty string as "all sources" (cross-source watch).
        string? pinnedSource = string.IsNullOrEmpty(sourceId) ? null : sourceId;

        // Idempotent: dedupe on (canId, signalName, sourceId).
        var canIdHex = FormatCanIdHex(maskedId);
        var existing = WatchedSignals.FirstOrDefault(w =>
            !w.IsPlaceholder
            && w.CanIdHex == canIdHex
            && w.SignalName == signalName
            && w.SourceId == pinnedSource);
        if (existing is not null) return;

        var row = new WatchedSignalRow(
            canIdHex: canIdHex,
            messageName: msg.Name,
            signalName: signalName,
            unit: sig.Unit,
            sourceId: pinnedSource);
        // v3.16.2 PATCH: back to the original single-call semantics —
        // Add + RefreshFrameCounts + Plot + remove placeholder all in
        // one pass. Safe when called once (tests, programmatic) — the
        // ItemContainerGenerator confusion only happens with rapid
        // bursts of multiple AddToWatch calls (the picker flow), which
        // uses the new AddToWatchForPicker + FinalizePickerAdds pair.
        WatchedSignals.Add(row);

        // v3.15.0 MINOR: refresh FrameCount + LatestValue for the new
        // row from the current bucket so the watch list immediately
        // shows how many frames are available.
        RefreshFrameCounts();

        // Auto-plot: the user just added this — show them the data
        // immediately. PlotSignalFromTableRow accepts a WatchedSignalRow.
        PlotSignalFromTableRow(row);

        // Drop any placeholder row when the first real watch entry is added.
        var placeholders = WatchedSignals.Where(w => w.IsPlaceholder).ToList();
        foreach (var ph in placeholders)
            WatchedSignals.Remove(ph);
    }

    /// <summary>
    /// v3.16.2 PATCH: picker-friendly AddToWatch that returns the
    /// created row. The caller collects all rows in a list, then
    /// invokes <see cref="FinalizePickerAdds"/> once to drop the
    /// placeholder + refresh frame counts + plot — keeping the
    /// WatchedSignals collection edit pattern as "N adds then 1
    /// remove" (WPF ItemContainerGenerator-friendly) rather than
    /// the previous "add + remove + add + add + remove" interleave.
    /// </summary>
    public WatchedSignalRow AddToWatchForPicker(uint canId, string signalName, string sourceId)
    {
        if (_dbcService.Current is null) return null!;
        var dbc = _dbcService.Current;
        var maskedId = canId & 0x7FFFFFFFu;
        var msg = dbc.Messages.FirstOrDefault(m => (m.Id & 0x7FFFFFFFu) == maskedId);
        if (msg is null) return null!;
        var sig = msg.Signals.FirstOrDefault(s => s.Name == signalName);
        if (sig is null) return null!;

        string? pinnedSource = string.IsNullOrEmpty(sourceId) ? null : sourceId;
        var canIdHex = FormatCanIdHex(maskedId);
        var existing = WatchedSignals.FirstOrDefault(w =>
            !w.IsPlaceholder
            && w.CanIdHex == canIdHex
            && w.SignalName == signalName
            && w.SourceId == pinnedSource);
        if (existing is not null) return existing;

        var row = new WatchedSignalRow(
            canIdHex: canIdHex,
            messageName: msg.Name,
            signalName: signalName,
            unit: sig.Unit,
            sourceId: pinnedSource);
        WatchedSignals.Add(row);
        return row;
    }

    /// <summary>
    /// v3.16.2 PATCH: finalize a batch of picker additions. Drops
    /// any placeholder, refreshes frame counts, and plots each
    /// added row. Designed to be called once after the picker
    /// returns, so the WatchedSignals collection has a single
    /// "add N rows" event followed by a single "remove placeholder"
    /// event (rather than the interleave that caused
    /// ItemContainerGenerator confusion in v3.16.1).
    /// <para>
    /// v3.16.6 PATCH BUGFIX (2-agent root-cause): the v3.16.2 design
    /// still fired N Add + 1 Remove events back-to-back, which races
    /// with the WPF ItemContainerGenerator's Recycling-mode bookkeeping
    /// when DataGrid EnableRowVirtualization=True. With 6+ signals
    /// selected in the picker, the Generator's cumulative count drifted
    /// by 1 from Items.Count ("累计计数 5 与实际计数 6 不相同") and
    /// threw InvalidOperationException on the next Refresh pass.
    /// <b>Fix:</b> collapse the picker finalize to a single Reset
    /// event (Clear) followed by all real rows in a deterministic
    /// order. Clear() forces the Generator to re-sync its cumulative
    /// count to 0 before the N Adds land — the cumulative count then
    /// matches Items.Count exactly. RefreshFrameCounts runs after
    /// the rebuild so FrameCount is correct on the first binding
    /// pass (no stale 0 values).
    /// </para>
    /// </summary>
    public void FinalizePickerAdds(IReadOnlyList<WatchedSignalRow> addedRows)
    {
        if (addedRows is null || addedRows.Count == 0)
        {
            // Even on no-op, ensure placeholder state is correct.
            EnsurePlaceholderRow();
            return;
        }

        // v3.16.6 PATCH: snapshot real rows (skip placeholder), Clear
        // the collection (1 Reset event → Generator re-syncs cumulative
        // count to 0), then re-Add the kept rows + the new picker rows
        // in a deterministic order. This avoids the N-Add + 1-Remove
        // interleave that races with WPF's Recycling generator
        // bookkeeping when DataGrid is virtualized.
        // v3.16.6 PATCH (B1 of this PATCH): dedupe addedRows against
        // `kept` by WatchId. AddToWatchForPicker returns the EXISTING
        // row (not a new one) when the user re-picks an already-watched
        // signal, and FinalizePickerAdds's contract is "addedRows are
        // rows to plot" — adding them all back to the collection would
        // double-list existing watches after the Clear. Match by WatchId
        // (the dedupe key) so the post-Clear state is identical to the
        // pre-Clear state + newly-added rows.
        var kept = WatchedSignals.Where(w => !w.IsPlaceholder).ToList();
        var keptIds = new HashSet<string>(kept.Select(r => r.WatchId), StringComparer.Ordinal);
        WatchedSignals.Clear();
        foreach (var row in kept) WatchedSignals.Add(row);
        foreach (var row in addedRows)
        {
            if (row is null) continue;
            if (keptIds.Contains(row.WatchId)) continue;  // already in kept — no re-add
            WatchedSignals.Add(row);
        }

        // Refresh frame counts for the watch list (now sees all rows).
        RefreshFrameCounts();

        // Plot each newly added row.
        foreach (var row in addedRows)
        {
            if (row is null) continue;
            PlotSignalFromTableRow(row);
        }
    }

    /// <summary>
    /// v3.15.0 MINOR: remove a watch entry. Unplots any chart series
    /// that came from this row, then removes from
    /// <see cref="WatchedSignals"/>.
    /// </summary>
    [RelayCommand]
    public void RemoveFromWatch(WatchedSignalRow row)
    {
        if (row is null) return;
        if (row.IsPlaceholder) return;
        UnplotSignalFromTableRow(row);
        WatchedSignals.Remove(row);
        EnsurePlaceholderRow();
    }

    /// <summary>
    /// v3.15.0 MINOR: ensure the watch list shows a contextual
    /// placeholder row when it's empty. Called from
    /// <see cref="RebuildSignalsCore"/> + <see cref="OnRegistrySourcesChanged"/>
    /// + <see cref="OnDbcLoaded"/> + <see cref="RemoveFromWatch"/>.
    /// </summary>
    private void EnsurePlaceholderRow()
    {
        // Don't add a duplicate placeholder.
        if (WatchedSignals.Any(w => w.IsPlaceholder)) return;
        var dbc = _dbcService.Current;
        var asc = _registry.Sources.Count;
        string msg;
        if (dbc is null && asc == 0)
            msg = "(no DBC and no .asc loaded — open DBC tab + File ▸ Add trace…)";
        else if (dbc is null)
            msg = "(no DBC loaded — open DBC from DBC tab to enable watch list)";
        else if (asc == 0)
            msg = "(no .asc loaded — File ▸ Add trace… to populate)";
        else
            msg = "(no signals in watch list — click + Add to watch…)";
        WatchedSignals.Add(new WatchedSignalRow(
            canIdHex: "—",
            messageName: msg,
            signalName: "",
            unit: "",
            isPlaceholder: true));
    }

    /// <summary>
    /// v3.15.0 MINOR: invoked by <see cref="TogglePlot"/> (legacy
    /// v3.14.3 path) and by <see cref="AddToWatch"/> (new v3.15.0
    /// path). Creates one <see cref="TraceChartSeries"/> per source
    /// that has matching frames. Graceful no-op if no source has
    /// frames (user can still toggle; nothing to chart).
    /// </summary>
    private void PlotSignalFromTableRow(WatchedSignalRow row)
    {
        if (_dbcService.Current is null) return;
        var dbc = _dbcService.Current;
        var dot = row.SignalKey.IndexOf('.');
        if (dot <= 0) return;
        var idHexStr = row.SignalKey.Substring(0, dot);
        if (!idHexStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return;
        if (!uint.TryParse(idHexStr.AsSpan(2),
                           System.Globalization.NumberStyles.HexNumber,
                           null, out var canId)) return;
        var lookupId = canId & 0x7FFFFFFFu;
        var sig = dbc.Messages
            .Where(m => (m.Id & 0x7FFFFFFFu) == lookupId)
            .SelectMany(m => m.Signals)
            .FirstOrDefault(s => s.Name == row.SignalName);
        if (sig is null) return;

        var created = 0;
        TraceChartSeries? firstBuilt = null;
        foreach (var source in _registry.Sources)
        {
            // v3.15.0 MINOR: source-pinned watches only plot against
            // their pinned source; cross-source watches (SourceId null)
            // plot all sources.
            if (row.SourceId is not null && source.SourceId != row.SourceId) continue;
            var built = BuildOneChartSeriesForSource(source, sig, lookupId, row.CanIdHex, row.SignalName);
            if (built is null) continue;  // no frames in this source
            ChartViewModel.AddSeries(built);
            created++;
            firstBuilt ??= built;
        }
        if (created > 0)
        {
            ChartViewModel.SyncYAxes();
            // v3.16.5 PATCH BUGFIX (4-agent root-cause): use the new
            // series' own XValues range, not the master service's
            // [CurrentTimestamp, TotalDuration]. The previous
            // CurrentTimestamp-based call overwrote EVERY series' X
            // axis (the loop in SyncXAxis iterates Series), and
            // CurrentTimestamp during playback = the live cursor
            // (e.g. 350s into a 650s trace), which narrowed the X
            // range to [350, 650] and pushed xs[0]..xs[N-1] frames
            // with x < 350 outside the viewport — OxyPlot rendered
            // the line off-canvas and the user saw "no chart".
            // Mirrors the working PlotSignal path at line 1725.
            var xMin = firstBuilt!.XValues[0];
            var xMax = firstBuilt.XValues[^1];
            // Defensive: if a degenerate series (single point), fall
            // back to master service's full range so OxyPlot has a
            // non-zero axis width.
            if (xMax <= xMin)
            {
                xMin = 0;
                xMax = _masterService?.TotalDuration > 0 ? _masterService.TotalDuration : 1.0;
            }
            ChartViewModel.SyncXAxis(xMin, xMax);
        }
    }

    /// <summary>
    /// v3.14.3 PATCH + v3.15.0 MINOR: remove all chart series whose
    /// <see cref="TraceChartSeries.SignalKey"/> matches
    /// <paramref name="row"/>.SignalKey. Inverse of
    /// <see cref="PlotSignalFromTableRow"/>.
    /// </summary>
    private void UnplotSignalFromTableRow(WatchedSignalRow row)
    {
        var key = row.SignalKey;
        // Snapshot because RemoveSeries mutates the collection.
        var matches = ChartViewModel.Series
            .Where(s => s.SignalKey == key
                        || s.SignalKey.EndsWith("." + key, StringComparison.Ordinal))
            .ToList();
        foreach (var s in matches)
            ChartViewModel.RemoveSeries(s);
    }

    // v3.4.3 PATCH: detach per-source INPC subscriptions. Idempotent —
    // subtracting an absent handler is a no-op (mirrors the existing
    // DetachAllServiceHandlers pattern).
    private void DetachAllSourcePropertyHandlers()
    {
        foreach (var src in _registry.Sources)
            src.PropertyChanged -= OnAnySourcePropertyChanged;
    }

    // v3.4.3 PATCH: react to TraceSource.CanIdFilter changes by
    // refreshing frame counts + removing orphan chart series
    // synchronously. The TraceSource instance only exposes CanIdFilter
    // as INPC today, so the filter guard is a safety net for future
    // fields. v3.14.3 PATCH: do NOT call RebuildSignalsCore — user
    // opt-ins in the signal table must survive filter changes; only
    // the per-row FrameCount + LatestValue columns are refreshed.
    private void OnAnySourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TraceSource.CanIdFilter)) return;
        if (_dbcService.Current is null) return;
        RefreshFrameCounts();
        RemoveOrphanChartSeries();
        ChartViewModel.SyncYAxes();
    }


    // v3.3.0 MINOR: master timeline is the clock; each non-master is
    // positioned at the proportional point of its own total duration.
    // Formula: nonMaster.t = (master.t / master.totalDuration) * nonMaster.totalDuration.
    // Clamp ratio to [0, 1] to handle transient slider overshoot.
    // v3.8.6 PATCH H1: also clamp masterT to [0, masterDur] before the
    // master branch svc.Seek(masterT) call. Symmetric-miss of the v3.8.4
    // L1 Replay-tab clamp; the comment at the call site said "ratio"
    // clamp handles overshoot, but the master direct-seek got the raw
    // (potentially out-of-range) value, leaving no frame in range after
    // the timeline walked past _frames.Count.
    private void SeekAllToProportionalTime(double masterT)
    {
        if (_masterService is null) return;
        var masterDur = _masterService.TotalDuration;
        if (masterDur <= 0)
        {
            // No total duration to clamp against -- defensively drop negatives.
            if (masterT < 0) masterT = 0;
            _masterService.Seek(masterT);
            return;
        }
        var clampedMasterT = Math.Clamp(masterT, 0.0, masterDur);
        var ratio = Math.Clamp(clampedMasterT / masterDur, 0.0, 1.0);
        foreach (var (sourceId, svc) in _allServices)
        {
            if (sourceId == MasterSourceId)
            {
                svc.Seek(clampedMasterT);
            }
            else
            {
                svc.Seek(ratio * svc.TotalDuration);
            }
        }
    }

    private void RebindMasterFromRegistry()
    {
        // Pure master-resolution step — caller (OnRegistrySourcesChanged)
        // owns the attach/detach lifecycle via AttachAllServiceHandlers /
        // DetachAllServiceHandlers. Keeping this method idempotent avoids
        // double-attaching the FrameEmitted + PlaybackEnded handlers when
        // invoked after a SourcesChanged event.
        if (_registry.Sources.Count == 0)
        {
            _masterService = null;
            MasterSourceId = "";
            return;
        }
        // Master invariant: prefer current MasterSourceId if still in Sources;
        // else fall back to Sources[0] (deterministic default).
        var newMaster = _registry.Sources.FirstOrDefault(
            s => s.SourceId == MasterSourceId) ?? _registry.Sources[0];
        MasterSourceId = newMaster.SourceId;
        _masterService = _allServices.TryGetValue(newMaster.SourceId, out var svc) ? svc : null;
    }

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

    /// <summary>
    /// v3.14.3 PATCH: build one chart subplot for one (source, signal)
    /// pair — the shared body for <see cref="PlotSignal(TraceChartSeries)"/>
    /// (placeholder replacement path) and <see cref="PlotSignalFromTableRow"/>
    /// (creation path). Returns the populated <see cref="TraceChartSeries"/>,
    /// or null if no matching frames exist in this source.
    /// <para>
    /// Honors the source's per-source <c>CanIdFilter</c> override so
    /// the chart matches what the user sees in the signal table's
    /// <c>N</c> column (consistent with the pre-v3.14.3 behavior
    /// where <c>BuildChartSeries</c> applied the same per-source
    /// resolution).
    /// </para>
    /// </summary>
    private TraceChartSeries? BuildOneChartSeriesForSource(
        TraceSource source, Signal sig, uint lookupId, string idHex, string sigName)
    {
        // v3.4.3 PATCH per-source filter override: if this source has
        // a non-empty per-source filter, use it as the allow-list;
        // otherwise inherit the global one.
        var globalAllowed = CanIdListParser.Parse(CanIdFilter).AllowList;
        var perSourceAllowed = CanIdListParser.Parse(source.CanIdFilter).AllowList;
        var effective = perSourceAllowed ?? globalAllowed;

        var frames = _registry.GetFrames(source.SourceId)
            .Where(f => (f.Id & 0x7FFFFFFFu) == lookupId
                        && (effective is null || effective.Contains(f.Id)))
            .OrderBy(f => f.Timestamp)
            .ToList();
        if (frames.Count == 0) return null;

        var xs = new double[frames.Count];
        var ys = new double[frames.Count];
        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;
        for (int i = 0; i < frames.Count; i++)
        {
            xs[i] = frames[i].Timestamp;
            ys[i] = SignalDecoder.Decode(frames[i].Data, sig);
            if (ys[i] < min) min = ys[i];
            if (ys[i] > max) max = ys[i];
        }

        var displayName = $"{source.DisplayName}.{idHex}.{sigName}";
        var plotModel = new PlotModel();
        // v3.16.9.2 PATCH: X-axis LabelFormatter formats ticks as wall-clock
        // when source carries a WallClockOrigin (parsed from ASC 'date' header);
        // otherwise falls back to a 3-tier elapsed formatter (>=1d / >=1h / <1h).
        // Spec: docs/superpowers/specs/2026-07-09-trace-viewer-enhancements-design.md
        // §3.4 lines 131-139. Uses InvariantCulture so locale cannot change
        // the 'MM/dd' ordering or the decimal point.
        //
        // NB: DateTimeKind.Local arithmetic does NOT normalize across DST
        // transitions. Traces spanning spring-forward may show one-hour gaps;
        // traces spanning fall-back may show repeated hours. Acceptable per
        // spec §7 (local time is the canonical interpretation of Vector's
        // 'date' header). v3.16.9.2 review-MEDIUM-2.
        //
        // NB: lambda captures the `source` reference, NOT the current
        // WallClockOrigin value (spec §3.4 R2). If source.WallClockOrigin is
        // mutated after this axis is created, the formatter re-resolves on
        // every LabelFormatter call so the new origin takes effect.
        // v3.16.9.2 review-HIGH.
        var bottomAxis = new LinearAxis { Position = AxisPosition.Bottom };
        bottomAxis.LabelFormatter = x =>
        {
            var o = source.WallClockOrigin;
            if (o is not null)
                return (o.Value + TimeSpan.FromSeconds(x))
                    .ToString("MM/dd HH:mm:ss", CultureInfo.InvariantCulture);
            // 3-tier elapsed fallback per spec §3.4 (>=1d / >=1h / <1h).
            // v3.16.9.2 review-MEDIUM-1: explicit InvariantCulture on all
            // branches so locale cannot change the decimal point.
            if (x >= 86400.0)
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:F1}d {1:hh\\:mm\\:ss}",
                    x / 86400.0,
                    TimeSpan.FromSeconds(x));
            if (x >= 3600.0)
                return TimeSpan.FromSeconds(x).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
            return TimeSpan.FromSeconds(x).ToString(@"mm\:ss\.f", CultureInfo.InvariantCulture);
        };
        plotModel.Axes.Add(bottomAxis);
        plotModel.Axes.Add(new LinearAxis { Position = AxisPosition.Left });
        // v3.16.4 PATCH BUGFIX: materialize the ItemsSource to a
        // List<DataPoint>. The previous deferred LINQ chain
        // (Enumerable.Range(...).Select(...)) was an IEnumerable that
        // OxyPlot's WPF binding machinery does not enumerate reliably
        // — the LineSeries would render with zero points. Forcing
        // .ToList() materializes the data so OxyPlot gets a stable
        // IList it can render.
        var dataPoints = new List<DataPoint>(frames.Count);
        for (int i = 0; i < frames.Count; i++)
            dataPoints.Add(new DataPoint(xs[i], ys[i]));
        // v3.16.9.2 PATCH: show discrete CAN sample points as circle markers
        // so the user can distinguish "trend line" (interpolation) from
        // "real CAN frame" (discrete event). MarkerSize=3 is small enough
        // not to occlude the line at 1920x1080. Spec §3.6.
        var line = new LineSeries
        {
            Color = source.Color,
            LineStyle = source.StrokeStyle,
            ItemsSource = dataPoints,
            MarkerType = MarkerType.Circle,
            MarkerSize = 3.0,
        };
        plotModel.Series.Add(line);
        // v3.16.9 PATCH: add a vertical LineAnnotation tagged "playback-cursor"
        // so ChartViewModel.UpdatePlaybackCursor (TraceChartViewModel.cs:86-100)
        // can find + reposition the red cursor line on every frame.
        // The cursor is a vertical line spanning the full Y axis at X = 0
        // (start of trace). The companion test
        // BuildOneChartSeriesForSource_CreatesPlaybackCursorLineAnnotation
        // pins this contract.
        // The bug was diagnosed in v3.16.6 release notes (line 42: "LineAnnotation
        // was never created") but never actually fixed.
        plotModel.Annotations.Add(new LineAnnotation
        {
            Type = LineAnnotationType.Vertical,
            X = 0.0,
            Color = OxyColors.Red,
            LineStyle = LineStyle.Solid,
            StrokeThickness = 1.5,
            Tag = "playback-cursor",
        });

        return new TraceChartSeries(
            SignalKey: $"{idHex}.{sigName}",
            DisplayName: displayName,
            Unit: sig.Unit,
            Color: source.Color,
            PlotModel: plotModel,
            XValues: xs,
            YValues: ys,
            MinValue: min,
            MaxValue: max,
            IsFocused: false,
            IsCollapsed: false,
            SourceId: source.SourceId,
            IsPlotPending: false);
    }

    /// <summary>
    /// Format a CAN ID as a hex string for display: "0x123" for standard
    /// (11-bit, IDE bit clear) and "0x00000123" for extended (29-bit,
    /// IDE bit set). Matches the DBC tab's ID display convention so the
    /// Trace Viewer signal list and the DBC message list line up
    /// visually.
    /// </summary>
    private static string FormatCanIdHex(uint id)
    {
        const uint IdeBit = 0x80000000u;
        return (id & IdeBit) == 0
            ? $"0x{id:X3}"
            : $"0x{id:X8}";
    }

    /// <summary>
    /// v3.14.2 PATCH: build the per-frame PlotModel + XValues + YValues
    /// for a single TraceChartSeries on demand. Called when the user
    /// opts a signal in (clicks the chart row's "Plot" affordance).
    /// The Trace Viewer registers a placeholder per (source, signal) row
    /// at load time and only decodes the per-frame data on user demand.
    /// This is the user-facing fix for the "Add Trace hangs 30+ seconds"
    /// bug — the prior eager build decoded 500K+ frames synchronously on
    /// the UI thread.
    /// <para>
    /// Implementation: matches the eager-build loop body verbatim but
    /// runs once per call instead of once per (source, msg, sig) tuple.
    /// Safe to call multiple times — clears the existing PlotModel first.
    /// </para>
    /// </summary>
    public void PlotSignal(TraceChartSeries series)
    {
        if (series is null) throw new ArgumentNullException(nameof(series));
        if (!series.IsPlotPending) return;  // already plotted

        // v3.14.3 PATCH: shared body via BuildOneChartSeriesForSource.
        // Parse SignalKey ("{idHex}.{sig.Name}") to recover the lookup
        // canId; the IDE-bit mask is applied at lookup time so it
        // matches the BucketFramesByCanId keys.
        var dot = series.SignalKey.IndexOf('.');
        if (dot <= 0) return;
        var idHexStr = series.SignalKey.Substring(0, dot);
        if (!idHexStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return;
        if (!uint.TryParse(idHexStr.AsSpan(2),
                           System.Globalization.NumberStyles.HexNumber,
                           null, out var canId)) return;
        var lookupId = canId & 0x7FFFFFFFu;

        var source = _registry.Sources.FirstOrDefault(s => s.SourceId == series.SourceId);
        if (source is null) return;

        // SignalKey is "{idHex}.{sig.Name}" — the sig.Name is the
        // LAST segment (post-dot). Lookup the Signal in the DBC.
        var dbc = _dbcService.Current;
        if (dbc is null) return;
        var sigName = series.SignalKey.Substring(dot + 1);
        var sig = dbc.Messages
            .Where(m => (m.Id & 0x7FFFFFFFu) == lookupId)
            .SelectMany(m => m.Signals)
            .FirstOrDefault(s => s.Name == sigName);
        if (sig is null) return;

        var built = BuildOneChartSeriesForSource(source, sig, lookupId, idHexStr, sigName);
        if (built is null) return;

        // Replace the placeholder in place (TraceChartSeries is a record;
        // we mutate the chart via the CollectionViewModel).
        var idx = ChartViewModel.Series.IndexOf(series);
        if (idx < 0) return;
        ChartViewModel.Series[idx] = built;
        // v3.14.2 PATCH: resync Y axes + X axis now that the series
        // has real data. RebuildSignalsCore's initial SyncYAxes ran
        // against the empty placeholder, leaving axes at default
        // (NaN). Re-run after the lazy fill so the chart renders
        // with the correct ranges on the very first opt-in.
        ChartViewModel.SyncYAxes();
        ChartViewModel.SyncXAxis(built.XValues[0], built.XValues[^1]);
    }

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

/// <summary>
/// v3.6.4 PATCH: no-op <see cref="IAscContentHasher"/> used when no
/// hasher was injected. Returns the empty string for every path so
/// <c>BuildSnapshot</c> never blocks on disk I/O and every saved
/// bundle round-trips without a contentHash. Production DI wires
/// <see cref="Sha256AscContentHasher"/>; tests that care about hashing
/// inject their own fake.
/// </summary>
internal sealed class NullAscContentHasher : IAscContentHasher
{
    public static readonly NullAscContentHasher Instance = new();
    private NullAscContentHasher() { }
    public Task<string> ComputeAsync(string path, CancellationToken ct = default)
        => Task.FromResult("");
}

/// <summary>
/// v3.6.4 PATCH: no-op <see cref="IAscLocator"/> used when no locator
/// was injected. Always returns <c>null</c> so the ApplySnapshotAsync
/// hash fallback is a no-op and the existing path-only resolution
/// continues to surface the missing-path list. Production DI wires
/// <see cref="FileSystemAscLocator"/>.
/// </summary>
internal sealed class NullAscLocator : IAscLocator
{
    public static readonly NullAscLocator Instance = new();
    private NullAscLocator() { }
    public Task<string?> LocateAsync(string contentHash, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
}
