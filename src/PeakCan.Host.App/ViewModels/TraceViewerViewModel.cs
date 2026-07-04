using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.Core.Dbc;
using PeakCan.Host.Core.Replay;

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

    public ObservableCollection<TraceSignalRow> Signals { get; } = new();
    public TraceChartViewModel ChartViewModel { get; } = new();

    /// <summary>v3.2.0 MINOR: read-through to the registry. XAML binds the
    /// legend strip against this property (one entry per loaded source).</summary>
    public IReadOnlyList<TraceSource> Sources => _registry.Sources;

    public TraceViewerViewModel(
        ITraceSessionRegistry registry,
        DbcService dbcService,
        ILogger<TraceViewerViewModel> logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _dbcService = dbcService ?? throw new ArgumentNullException(nameof(dbcService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _syncContext = SynchronizationContext.Current;
        _registry.SourcesChanged += OnRegistrySourcesChanged;
        // Initial pull — captures any pre-loaded sources (none in normal startup).
        // OnRegistrySourcesChanged populates _allServices and rebinds master;
        // a bare RebindMasterFromRegistry would leave _allServices empty.
        OnRegistrySourcesChanged();
    }

    /// <summary>
    /// v3.2.0 MINOR: append a new trace to the session. Used by the
    /// View's "Add trace…" button. Surfaces <see cref="ReplayException"/>
    /// to the caller (window shows MessageBox); other exception types
    /// propagate unhandled.
    /// </summary>
    [RelayCommand]
    public async Task AddTraceAsync(string path)
    {
        try
        {
            await _registry.LoadAsync(path).ConfigureAwait(true);
            // The SourcesChanged handler will RebindMasterFromRegistry +
            // refresh ChartViewModel duration.
        }
        catch (ReplayException ex)
        {
            LogLoadFailed(_logger, ex, path);
            throw;
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
    /// v3.2.0 MINOR: read-only proxy for the legacy v3.0
    /// <c>OpenFileAsync</c> command name. Calls <see cref="AddTraceAsync"/>
    /// internally — for a single-source session the effect is identical to
    /// v3.0.0 (one ASC loaded).
    /// </summary>
    [RelayCommand]
    public Task OpenFileAsync(string path) => AddTraceAsync(path);

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

    /// <summary>
    /// Load a DBC into <see cref="DbcService"/>. Updates
    /// <see cref="LoadedDbcPath"/>; <see cref="RebuildSignalsAsync"/>
    /// picks up the new document on next signal rebuild.
    /// </summary>
    [RelayCommand]
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
        // Setting ScrubberValue fires OnScrubberValueChanged, which calls
        // SeekAllToProportionalTime. Doing the seek here too would double-call
        // every service — single source of truth is the scrubber change handler.
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
    private void SeekAllToProportionalTime(double masterT)
    {
        if (_masterService is null) return;
        var masterDur = _masterService.TotalDuration;
        if (masterDur <= 0)
        {
            _masterService.Seek(masterT);
            return;
        }
        var ratio = Math.Clamp(masterT / masterDur, 0.0, 1.0);
        foreach (var (sourceId, svc) in _allServices)
        {
            if (sourceId == MasterSourceId)
            {
                svc.Seek(masterT);
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
        var allowed = CanIdFilterParser.Parse(CanIdFilter);
        var dbc = _dbcService.Current;
        if (dbc is null)
        {
            // No DBC — nothing to decode against.
            return;
        }

        // v3.2.0 MINOR: bucket frames from all loaded sources by CAN ID.
        var byId = new Dictionary<uint, List<ReplayFrame>>();
        foreach (var source in _registry.Sources)
        {
            // v3.4.3 PATCH: per-source filter overrides the global one. Empty
            // per-source → fall through to globalAllowed (inherit). Non-empty
            // → use the per-source parse result exclusively.
            var perSourceAllowed = CanIdFilterParser.Parse(source.CanIdFilter);
            var effective = perSourceAllowed ?? allowed;
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
        foreach (var row in rows)
        {
            Signals.Add(row);
        }

        // v3.4.0 MINOR: emit one TraceChartSeries per (source, signal) pair.
        // Per-source re-group: the byId dict above is global (across sources);
        // chart series need per-source frames per CAN ID, so re-group here.
        // Independent of the Signals population loop above — the two loops
        // share dbc.Messages but read from different frame buckets.
        foreach (var source in _registry.Sources)
        {
            // v3.4.3 PATCH: same per-source resolution as the byId loop above.
            var perSourceAllowed = CanIdFilterParser.Parse(source.CanIdFilter);
            var effective = perSourceAllowed ?? allowed;
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

        // v3.4.0 MINOR: synchronize axes now that all subplots exist.
        ChartViewModel.SyncYAxes();
        ChartViewModel.SyncXAxis(0, _masterService?.TotalDuration ?? 0);
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
