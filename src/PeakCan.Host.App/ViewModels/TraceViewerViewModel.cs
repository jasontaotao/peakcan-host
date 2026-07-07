using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using OxyPlot;
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
/// One row in the left-side signal list. Static per loaded trace; the
/// LatestValue column is updated as the playback cursor moves.
/// </summary>
public sealed record TraceSignalRow(
    string CanIdHex,
    string SignalName,
    string Unit,
    bool IsPlotted,
    double LatestValue);

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
    public TraceChartViewModel ChartViewModel { get; } = new();

    /// <summary>v3.2.0 MINOR: read-through to the registry. XAML binds the
    /// legend strip against this property (one entry per loaded source).</summary>
    public IReadOnlyList<TraceSource> Sources => _registry.Sources;

    public TraceViewerViewModel(
        ITraceSessionRegistry registry,
        DbcService dbcService,
        ILogger<TraceViewerViewModel> logger,
        TraceSessionLibrary sessionLibrary,
        IFileDialogService? fileDialog = null,
        IAscContentHasher? hasher = null,
        IAscLocator? locator = null)
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
        _syncContext = SynchronizationContext.Current;
        _registry.SourcesChanged += OnRegistrySourcesChanged;
        // Initial pull — captures any pre-loaded sources (none in normal startup).
        // OnRegistrySourcesChanged populates _allServices and rebinds master;
        // a bare RebindMasterFromRegistry would leave _allServices empty.
        OnRegistrySourcesChanged();
    }

    /// <summary>
    /// v3.2.0 MINOR: append a new trace to the session. v3.9.1 PATCH
    /// Bug #2: now absorbs failures into bindable state
    /// (<see cref="ErrorMessage"/> + <see cref="StatusMessage"/>) instead
    /// of rethrowing into the View's <c>async void</c> click handler.
    /// Mirrors <see cref="ReplayViewModel.OpenAsync"/>'s try/catch shape:
    /// <list type="bullet">
    ///   <item><see cref="OperationCanceledException"/> → swallowed,
    ///     <c>StatusMessage = "Load cancelled"</c>, no ErrorMessage.</item>
    ///   <item><see cref="ReplayException"/> → <c>ErrorMessage = ex.Message</c>,
    ///     <c>StatusMessage = "Load failed"</c>.</item>
    /// </list>
    /// <see cref="IsLoading"/> flips true → false in <c>finally</c> so the
    /// toolbar button re-enables regardless of success or failure path.
    /// <para>
    /// v3.9.1 PATCH: <c>CanExecute = nameof(CanAddTrace)</c> so the
    /// generated <c>AddTraceCommand</c> respects <see cref="IsLoading"/> —
    /// the toolbar button greys out during a load.
    /// </para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAddTrace))]
    public async Task AddTraceAsync(string path)
    {
        try
        {
            ErrorMessage = null;
            IsLoading = true;
            var name = System.IO.Path.GetFileName(path);
            StatusMessage = $"Loading {name}…";
            await _registry.LoadAsync(path).ConfigureAwait(true);
            StatusMessage = $"Loaded {name}";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Load cancelled";
        }
        catch (ReplayException ex)
        {
            LogLoadFailed(_logger, ex, path);
            ErrorMessage = ex.Message;
            StatusMessage = "Load failed";
        }
        // v3.9.2 PATCH H10: defensive fallback catch. AddTraceAsync is
        // invoked through an async-void command (the source-gen
        // AddTraceCommand), so any exception that escapes the typed
        // arms above would propagate to WPF DispatcherUnhandledException,
        // where App.xaml.cs:332 deliberately does NOT mark Handled —
        // resulting in process termination. TraceViewerService.LoadAsync
        // already wraps nearly every I/O failure in ReplayLoadException,
        // but a registry hook (e.g. SourcesChanged listener throwing)
        // or an unexpected exception in ApplyAutoSnapshotAsync could
        // still escape. Log + ErrorMessage + StatusMessage keeps the
        // user in control instead of killing the app.
        catch (Exception ex)
        {
            LogLoadFailed(_logger, ex, path);
            ErrorMessage = $"Unexpected error: {ex.Message}";
            StatusMessage = "Load failed";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// v3.2.0 MINOR: remove a source from the session. Invoked by the
    /// per-source "✕" button in the legend strip.
    /// </summary>
    [RelayCommand]
    public async Task RemoveTraceAsync(string sourceId)
    {
        await _registry.UnloadAsync(sourceId).ConfigureAwait(true);
    }

    /// <summary>
    /// v3.9.1 PATCH Bug #2: <c>CanExecute</c> predicate for
    /// <see cref="AddTraceCommand"/>. Disables the toolbar button while a
    /// load is in flight.
    /// </summary>
    private bool CanAddTrace(string? path) => !IsLoading;

    /// <summary>
    /// v3.3.0 MINOR: switch the master source mid-session. Stops playback,
    /// swaps the master, restarts if was playing. If the new sourceId is
    /// not in <see cref="_allServices"/> the call is a no-op. After the
    /// swap the previous master's <c>PlaybackEnded</c> handler is detached
    /// and the new master's is attached (via the standard attach/detach
    /// lifecycle) so the loop rewind anchor follows the active master.
    /// </summary>
    [RelayCommand]
    public void SetMaster(string sourceId)
    {
        if (sourceId == MasterSourceId) return;
        if (!_allServices.TryGetValue(sourceId, out var newMaster)) return;
        var wasPlaying = _masterService?.State == ReplayState.Playing;
        Stop();   // resets all services to t=0
        MasterSourceId = sourceId;
        _masterService = newMaster;
        TotalDuration = _masterService.TotalDuration;
        ChartViewModel.SetTotalDuration(TotalDuration);
        // Reattach event handlers — the previous master had FrameEmitted +
        // PlaybackEnded subscribed; the new master needs the same hooks.
        DetachAllServiceHandlers();
        AttachAllServiceHandlers();
        PropagateLoopToAllServices();
        PropagateSpeedToAllServices();
        // Master swap can change which signal rows have data (different
        // frame set); rebuild off-thread to avoid blocking the UI.
        _ = RebuildSignalsAsync();
        if (wasPlaying) Play();
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to load trace: {Path}")]
    private static partial void LogLoadFailed(ILogger logger, Exception ex, string path);

    // v3.5.0 MINOR: bundle-load could not resolve one of the recorded .asc
    // paths (file moved, deleted, or on a currently-unmounted drive). The
    // caller (View) surfaces the missing paths via a MessageBox so the user
    // can decide whether to remap or proceed without.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Bundle source missing or unreadable: {Path}")]
    private static partial void LogSourceMissing(ILogger logger, string path, Exception ex);

    // v3.6.4 PATCH: hash-based relocation recovered a missing .asc.
    [LoggerMessage(Level = LogLevel.Information, Message = "Bundle source relocated via content hash: {OldPath} -> {NewPath}")]
    private static partial void LogRelocated(ILogger logger, string oldPath, string newPath);

    // v3.9.2 PATCH L1: source-gen'd log helper for the bundle DBC load
    // fallback catch (was bare catch { } before).
    [LoggerMessage(Level = LogLevel.Warning, Message = "Bundle DBC load failed for {Path}")]
    private static partial void LogBundleDbcLoadFailed(ILogger logger, string path, Exception ex);

    /// <summary>
    /// Load a DBC into <see cref="DbcService"/>. Updates
    /// <see cref="LoadedDbcPath"/>; <see cref="RebuildSignalsAsync"/>
    /// picks up the new document on next signal rebuild.
    /// <para>
    /// v3.9.2 PATCH H2: was <c>[RelayCommand]</c>-attributed but XAML
    /// wires <c>Click="OnLoadDbcClick"</c> (calls method directly) —
    /// the source-gen <c>LoadDbcCommand</c> property had no consumer.
    /// Method stays as a public API (consumed by code-behind + 11
    /// tests + chart/filter test classes); the RelayCommand wrapper
    /// is dropped.
    /// </para>
    /// </summary>
    public async Task LoadDbcAsync(string path)
    {
        await _dbcService.LoadAsync(path).ConfigureAwait(true);
        LoadedDbcPath = path;
        await RebuildSignalsAsync().ConfigureAwait(true);
    }

    /// <summary>v3.2.0 MINOR: XAML binding source for the legend strip's
    /// <c>Visibility</c>. True when at least one trace is loaded.</summary>
    public bool HasSources => Sources.Count > 0;

    [RelayCommand]
    public void Play()
    {
        foreach (var svc in _allServices.Values)
            svc.Play();
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
        if (TotalDuration > 0 && _masterService is not null)
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

    // v3.4.2 PATCH: filter changes trigger a synchronous rebuild via the
    // extracted core. Property change notifications fire on the UI thread,
    // and the core is fully synchronous — no Task continuation race.
    partial void OnCanIdFilterChanged(string value)
    {
        RebuildSignalsCore();
    }

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
    /// </summary>
    public TraceSessionBundleDto BuildSnapshot()
    {
        var dto = new TraceSessionBundleDto
        {
            Version = 1,
            Schema = TraceSessionLibrary.CurrentSchema,
            SavedAt = DateTimeOffset.UtcNow,
            AppVersion = GetAppVersion(),
            DbcPath = LoadedDbcPath ?? "",
            GlobalCanIdFilter = CanIdFilter ?? "",
        };
        dto.Sources = new List<BundleSourceDto>(Sources.Count);
        foreach (var src in Sources)
        {
            // v3.6.4 PATCH: populate contentHash when the source's
            // .asc still exists on disk so the bundle can later be
            // relocated via the SHA-256 lookup. Hashing is synchronous
            // here because BuildSnapshot is invoked from a
            // Task.Run-wrapped save (SaveSessionAsync wraps the call);
            // we await it inline below.
            var hash = "";
            if (!string.IsNullOrEmpty(src.Path) && File.Exists(src.Path))
            {
                try
                {
                    hash = _hasher.ComputeAsync(src.Path).GetAwaiter().GetResult();
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
                LogBundleDbcLoadFailed(_logger, dto.DbcPath, ex);
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
    /// v3.2.0 MINOR: react to <see cref="ITraceSessionRegistry.SourcesChanged"/>
    /// — re-pin master to first source, update TotalDuration + ChartViewModel
    /// duration, refresh LoadedTracePath (legacy binding). v3.3.0 MINOR:
    /// attach FrameEmitted + master PlaybackEnded handlers and propagate
    /// Loop/Speed to every newly registered service.
    /// </summary>
    private void OnRegistrySourcesChanged()
    {
        DetachAllServiceHandlers();
        DetachAllSourcePropertyHandlers();   // v3.4.3 PATCH
        _allServices.Clear();
        foreach (var src in _registry.Sources)
        {
            var svc = _registry.GetService(src.SourceId);
            if (svc is null) continue;
            _allServices[src.SourceId] = svc;
            // v3.4.3 PATCH: subscribe to per-source filter changes (manual
            // INPC on TraceSource.CanIdFilter). Detach happens first above;
            // re-attaching here is safe even if the registry contains the
            // same instance across consecutive SourcesChanged events.
            src.PropertyChanged += OnAnySourcePropertyChanged;
            // Multi-trace sync mode: ignore per-source playback range
            // (each source's playable range = full [0, TotalDuration]).
            if (_registry.Sources.Count > 1)
            {
                svc.StartTimestamp = null;
                svc.EndTimestamp = null;
            }
        }
        RebindMasterFromRegistry();
        AttachAllServiceHandlers();
        PropagateLoopToAllServices();
        PropagateSpeedToAllServices();
        OnPropertyChanged(nameof(Sources));
        OnPropertyChanged(nameof(HasSources));
        LoadedTracePath = Sources.Count > 0 ? Sources[0].Path : "";
        TotalDuration = _masterService?.TotalDuration ?? 0.0;
        ChartViewModel.SetTotalDuration(TotalDuration);
        ChartViewModel.Series.Clear();
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
    // rebuilding the chart + signal rows synchronously. The TraceSource
    // instance only exposes CanIdFilter as INPC today, so the filter
    // guard is a safety net for future fields.
    private void OnAnySourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TraceSource.CanIdFilter)) return;
        RebuildSignalsCore();
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

    private void AttachAllServiceHandlers()
    {
        foreach (var (sourceId, svc) in _allServices)
        {
            svc.FrameEmitted += OnAnyFrameEmitted;
            // Only the master drives the loop rewind — non-masters use
            // Loop=false so they don't independently wrap (avoids per-timer drift).
            if (sourceId == MasterSourceId)
                svc.PlaybackEnded += OnMasterPlaybackEnded;
        }
    }

    private void DetachAllServiceHandlers()
    {
        foreach (var svc in _allServices.Values)
        {
            svc.FrameEmitted -= OnAnyFrameEmitted;
            // PlaybackEnded was only subscribed on master — detach defensively
            // (idempotent on services that never had the handler).
            svc.PlaybackEnded -= OnMasterPlaybackEnded;
        }
    }

    // v3.3.0 MINOR: single master-driven rewind anchor. When master EOFs
    // with Loop=true, rewind all services proportionally and resume play.
    private void OnMasterPlaybackEnded(object? sender, PlaybackEndedEventArgs e)
    {
        if (!Loop) return;
        if (e.Error is not null) return;   // sink error / abnormal end — don't auto-loop
        SeekAllToProportionalTime(0.0);
        foreach (var svc in _allServices.Values)
            if (svc.State != ReplayState.Playing)
                svc.Play();
    }

    /// <summary>
    /// FrameEmitted is invoked on the timeline's timer thread. We Post
    /// the cursor advance to the captured
    /// <see cref="SynchronizationContext"/> so the binding writes happen
    /// on the UI thread. Test path (no captured context) sets the
    /// cursor directly — safe because tests assert immediately after
    /// raising the event.
    /// </summary>
    private void OnAnyFrameEmitted(ReplayFrame frame)
    {
        if (_syncContext is not null)
            _syncContext.Post(_ => ChartViewModel.UpdatePlaybackCursor(_masterService?.CurrentTimestamp ?? 0.0), null);
        else
            ChartViewModel.UpdatePlaybackCursor(_masterService?.CurrentTimestamp ?? 0.0);
    }

    /// <summary>
    /// Rebuild the left-side <see cref="Signals"/> collection from the
    /// currently loaded trace + (optional) DBC. v3.2.0 MINOR: walks
    /// <see cref="ITraceSessionRegistry.GetFrames"/> per source so multi-trace
    /// overlays see all frames across all loaded sources.
    /// </summary>
    private async Task RebuildSignalsAsync()
    {
        RebuildSignalsCore();
        await Task.CompletedTask;
    }

    /// <summary>
    /// v3.4.2 PATCH: synchronous core of <see cref="RebuildSignalsAsync"/>.
    /// Extracted so property-setter change handlers (synchronous context)
    /// can invoke the rebuild without the async-Task continuation race.
    /// The body has no real awaits — it's fully synchronous in practice.
    /// </summary>
    private void RebuildSignalsCore()
    {
        Signals.Clear();
        ChartViewModel.Series.Clear();   // v3.4.0 MINOR: also clear chart series
        // v3.4.2 PATCH: parse the global filter once per rebuild. null = no filter.
        var allowed = CanIdListParser.Parse(CanIdFilter).AllowList;
        var dbc = _dbcService.Current;
        if (dbc is null)
        {
            // No DBC — nothing to decode against.
            return;
        }

        // v3.11.0 MINOR T4 (H8): 145-LoC body split into 3 sub-methods.
        // Behavior preserved exactly — same filter resolution, same sort,
        // same chart-series construction, same axes-sync finalization.
        var byId = BucketFramesByCanId(allowed);
        var rows = BuildSignalRows(byId, dbc);
        foreach (var row in rows)
        {
            Signals.Add(row);
        }
        BuildChartSeries(allowed, dbc);

        // v3.4.0 MINOR: synchronize axes now that all subplots exist.
        ChartViewModel.SyncYAxes();
        ChartViewModel.SyncXAxis(0, _masterService?.TotalDuration ?? 0);
    }

    /// <summary>
    /// v3.11.0 MINOR T4 (H8): bucket all loaded frames by CAN ID across
    /// every registered source, applying per-source overrides of the
    /// global allow-list. Returns a dict from CAN ID → ordered list of
    /// matching <see cref="ReplayFrame"/>s (insertion order = source
    /// iteration order, which matches the registry's order). Replaces
    /// the first half of the original 145-LoC <c>RebuildSignalsCore</c>
    /// body.
    /// </summary>
    private Dictionary<uint, List<ReplayFrame>> BucketFramesByCanId(IReadOnlySet<uint>? globalAllowed)
    {
        // v3.2.0 MINOR: bucket frames from all loaded sources by CAN ID.
        var byId = new Dictionary<uint, List<ReplayFrame>>();
        foreach (var source in _registry.Sources)
        {
            // v3.4.3 PATCH: per-source filter overrides the global one. Empty
            // per-source → fall through to globalAllowed (inherit). Non-empty
            // → use the per-source parse result exclusively.
            var perSourceAllowed = CanIdListParser.Parse(source.CanIdFilter).AllowList;
            var effective = perSourceAllowed ?? globalAllowed;
            foreach (var f in _registry.GetFrames(source.SourceId))
            {
                if (effective is not null && !effective.Contains(f.Id)) continue;
                if (!byId.TryGetValue(f.Id, out var list))
                {
                    list = new List<ReplayFrame>();
                    byId[f.Id] = list;
                }
                list.Add(f);
            }
        }
        return byId;
    }

    /// <summary>
    /// v3.11.0 MINOR T4 (H8): walk <paramref name="dbc"/>.Messages and
    /// produce one <see cref="TraceSignalRow"/> per signal for every
    /// message with at least one matching frame in
    /// <paramref name="byId"/>. The LatestValue column is decoded from
    /// the LAST matching frame in each CAN-ID bucket so the column
    /// reflects the most recent sample (matches the pre-refactor v3.2.0
    /// semantics). Rows are returned sorted by
    /// (CanIdHex, SignalName) ordinal order — also matches the
    /// pre-refactor v3.2.0 sort key.
    /// </summary>
    private List<TraceSignalRow> BuildSignalRows(
        Dictionary<uint, List<ReplayFrame>> byId,
        DbcDocument dbc)
    {
        var rows = new List<TraceSignalRow>();
        foreach (var msg in dbc.Messages)
        {
            if (!byId.TryGetValue(msg.Id, out var matching) || matching.Count == 0)
            {
                continue;
            }
            var idHex = FormatCanIdHex(msg.Id);
            foreach (var sig in msg.Signals)
            {
                // Latest = decoded value of the last matching frame.
                // Use the existing decode path so signed/float/factor/offset
                // semantics match the live Trace Chart VM exactly.
                var lastFrame = matching[^1];
                var value = SignalDecoder.Decode(lastFrame.Data, sig);
                rows.Add(new TraceSignalRow(
                    CanIdHex: idHex,
                    SignalName: sig.Name,
                    Unit: sig.Unit,
                    IsPlotted: false,
                    LatestValue: value));
            }
        }

        rows.Sort(static (a, b) =>
        {
            var byId2 = string.CompareOrdinal(a.CanIdHex, b.CanIdHex);
            return byId2 != 0 ? byId2 : string.CompareOrdinal(a.SignalName, b.SignalName);
        });
        return rows;
    }

    /// <summary>
    /// v3.11.0 MINOR T4 (H8): emit one <see cref="TraceChartSeries"/>
    /// per (source, message, signal) triple whose per-source bucket
    /// contains at least one frame. The per-source re-group (a
    /// per-source bucket dict) is required because the chart's
    /// <see cref="TraceChartSeries"/> is per-source — the global
    /// <see cref="BucketFramesByCanId"/> output spans all sources and
    /// can't be used directly. The per-source filter resolution
    /// mirrors the bucket loop so behavior is preserved exactly.
    /// </summary>
    private void BuildChartSeries(
        IReadOnlySet<uint>? globalAllowed,
        DbcDocument dbc)
    {
        // v3.4.0 MINOR: emit one TraceChartSeries per (source, signal) pair.
        // Per-source re-group: chart series need per-source frames per CAN
        // ID, so re-group from the registry here (independent of the Signals
        // population loop above — the two loops share dbc.Messages but read
        // from different frame buckets).
        foreach (var source in _registry.Sources)
        {
            // v3.4.3 PATCH: same per-source resolution as the byId loop above.
            var perSourceAllowed = CanIdListParser.Parse(source.CanIdFilter).AllowList;
            var effective = perSourceAllowed ?? globalAllowed;
            var srcById = new Dictionary<uint, List<ReplayFrame>>();
            foreach (var f in _registry.GetFrames(source.SourceId))
            {
                if (effective is not null && !effective.Contains(f.Id)) continue;
                if (!srcById.TryGetValue(f.Id, out var list))
                {
                    list = new List<ReplayFrame>();
                    srcById[f.Id] = list;
                }
                list.Add(f);
            }
            foreach (var msg in dbc.Messages)
            {
                if (!srcById.TryGetValue(msg.Id, out var matching) || matching.Count == 0)
                    continue;
                var idHex = FormatCanIdHex(msg.Id);
                foreach (var sig in msg.Signals)
                {
                    var xs = new List<double>(matching.Count);
                    var ys = new List<double>(matching.Count);
                    foreach (var f in matching)
                    {
                        xs.Add(f.Timestamp);
                        ys.Add(SignalDecoder.Decode(f.Data, sig));
                    }
                    var plotModel = new PlotModel();
                    plotModel.Axes.Add(new LinearAxis
                    {
                        Position = AxisPosition.Bottom,
                        Title = "Time (s)",
                    });
                    plotModel.Axes.Add(new LinearAxis
                    {
                        Position = AxisPosition.Left,
                        Title = sig.Unit,
                    });
                    var line = new LineSeries
                    {
                        Title = $"{source.DisplayName}/{sig.Name}",
                        Color = source.Color,
                        // v3.4.0 MINOR: stroke style for color-blind accessibility.
                        LineStyle = source.StrokeStyle,
                    };
                    for (int i = 0; i < xs.Count; i++)
                        line.Points.Add(new DataPoint(xs[i], ys[i]));
                    plotModel.Series.Add(line);
                    var displayName = $"{source.DisplayName}.{idHex}.{sig.Name}";
                    ChartViewModel.AddSeries(new TraceChartSeries(
                        SignalKey: $"{idHex}.{sig.Name}",
                        DisplayName: displayName,
                        Unit: sig.Unit,
                        Color: source.Color,
                        PlotModel: plotModel,
                        XValues: xs,
                        YValues: ys,
                        MinValue: ys.Min(),
                        MaxValue: ys.Max(),
                        IsFocused: false,
                        IsCollapsed: false,
                        SourceId: source.SourceId));
                }
            }
        }
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
    /// v3.6.0 MINOR T1.A: read version from assembly metadata instead of
    /// a hardcoded string. Mirrors the
    /// <see cref="AppShellViewModel.WindowTitle"/> pattern. Strip a
    /// trailing "+git&lt;sha&gt;" suffix that LocalBuilder adds so the
    /// bundle round-trips cleanly across builds.
    /// </summary>
    private static string GetAppVersion()
    {
        var info = typeof(App).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (string.IsNullOrEmpty(info)) return "0.0.0";
        var plus = info.IndexOf('+');
        return plus > 0 ? info[..plus] : info;
    }

    /// <summary>
    /// Unsubscribe from the registry + master service and stop playback.
    /// Safe to call multiple times — <c>_disposed</c> guards re-entry.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
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
