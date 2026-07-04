using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
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
/// <b>Multi-trace mode (Sources.Count &gt; 1):</b> playback commands
/// (<see cref="PlayCommand"/>, <see cref="PauseCommand"/>, <see cref="StopCommand"/>,
/// <see cref="SeekToCommand"/>) throw <see cref="InvalidOperationException"/>
/// because sync playback across N traces is deferred to v3.3.0 (proportional
/// seek math + master-source dropdown UI is non-trivial). The View hides
/// the play/pause/stop controls when <see cref="IsMultiTraceMode"/> is true.
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
        RebindMasterFromRegistry();
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
            // update IsMultiTraceMode + refresh ChartViewModel duration.
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

    /// <summary>v3.2.0 MINOR: computed from <see cref="Sources"/>.Count. Re-raised
    /// via OnSourcesPropertyChanged whenever SourcesChanged fires.</summary>
    public bool IsMultiTraceMode => Sources.Count > 1;

    /// <summary>v3.2.0 MINOR: XAML binding source for the legend strip's
    /// <c>Visibility</c>. True when at least one trace is loaded.</summary>
    public bool HasSources => Sources.Count > 0;

    /// <summary>Returns true when more than one trace is loaded (multi-trace mode is active).</summary>
    public bool IsPlaybackDisabled => IsMultiTraceMode || Sources.Count == 0;

    /// <summary>v3.2.0 MINOR: XAML binding source for the play/pause/stop
    /// buttons' <c>Visibility</c>. Visible when single-trace playback is
    /// allowed; collapsed in multi-trace mode or when no source is loaded.</summary>
    public System.Windows.Visibility PlaybackControlsVisibility =>
        IsPlaybackDisabled ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    private void EnsureSingleTraceMode()
    {
        if (IsMultiTraceMode)
            throw new InvalidOperationException(
                "Playback disabled in multi-trace mode (v3.3.0 will add sync playback across N sources).");
    }

    [RelayCommand]
    public void Play()
    {
        EnsureSingleTraceMode();
        _masterService?.Play();
    }

    [RelayCommand]
    public void Pause()
    {
        EnsureSingleTraceMode();
        _masterService?.Pause();
    }

    [RelayCommand]
    public void Stop()
    {
        EnsureSingleTraceMode();
        _masterService?.Stop();
        ScrubberValue = 0;
    }

    [RelayCommand]
    public void SeekTo(double t)
    {
        EnsureSingleTraceMode();
        _masterService?.Seek(t);
    }

    partial void OnScrubberValueChanged(double value)
    {
        if (TotalDuration > 0 && _masterService is not null && !IsMultiTraceMode)
            _masterService.Seek(value);
    }

    /// <summary>
    /// v3.2.0 MINOR: react to <see cref="ITraceSessionRegistry.SourcesChanged"/>
    /// — re-pin master to first source, update TotalDuration + ChartViewModel
    /// duration, refresh IsMultiTraceMode + LoadedTracePath (legacy binding).
    /// </summary>
    private void OnRegistrySourcesChanged()
    {
        RebindMasterFromRegistry();
        OnPropertyChanged(nameof(Sources));
        OnPropertyChanged(nameof(IsMultiTraceMode));
        OnPropertyChanged(nameof(IsPlaybackDisabled));
        OnPropertyChanged(nameof(PlaybackControlsVisibility));
        OnPropertyChanged(nameof(HasSources));
        LoadedTracePath = Sources.Count > 0 ? Sources[0].Path : "";
        TotalDuration = _masterService?.TotalDuration ?? 0.0;
        ChartViewModel.SetTotalDuration(TotalDuration);
        ChartViewModel.Series.Clear();
    }

    private void RebindMasterFromRegistry()
    {
        // Unhook the old master (if any).
        if (_masterService is not null)
            _masterService.FrameEmitted -= OnFrameEmitted;

        // Pin master to first source (deterministic for the session).
        if (_registry.Sources.Count == 0)
        {
            _masterService = null;
            MasterSourceId = "";
            return;
        }

        var master = _registry.Sources[0];
        MasterSourceId = master.SourceId;
        // Resolve the master service through a round-trip: in the registry
        // implementation, sources are keyed by SourceId. For simplicity,
        // we expose a per-source service lookup via the registry's internal
        // LoadAsync flow. To keep this lightweight, we re-use the existing
        // service via a new ITraceSessionRegistry.GetService(sourceId) call.
        // See ITraceSessionRegistry for the contract.
        _masterService = GetMasterService(master.SourceId);
        if (_masterService is not null)
            _masterService.FrameEmitted += OnFrameEmitted;
    }

    /// <summary>
    /// v3.2.0 MINOR: looks up the underlying <see cref="ITraceViewerService"/>
    /// for the master source. The current registry contract exposes only
    /// <see cref="ITraceSessionRegistry.GetFrames"/> (frames, not service);
    /// playback needs the service handle. We resolve via a public
    /// <see cref="ITraceSessionRegistry.GetService"/> helper added in the
    /// same PR. See <see cref="TraceSessionRegistry.GetService"/>.
    /// </summary>
    private ITraceViewerService? GetMasterService(string sourceId)
        => _registry.GetService(sourceId);

    /// <summary>
    /// FrameEmitted is invoked on the timeline's timer thread. We Post
    /// the cursor advance to the captured
    /// <see cref="SynchronizationContext"/> so the binding writes happen
    /// on the UI thread. Test path (no captured context) sets the
    /// cursor directly — safe because tests assert immediately after
    /// raising the event.
    /// </summary>
    private void OnFrameEmitted(ReplayFrame frame)
    {
        if (_syncContext is not null)
        {
            _syncContext.Post(_ =>
            {
                if (_masterService is not null)
                    ChartViewModel.UpdatePlaybackCursor(_masterService.CurrentTimestamp);
            }, null);
        }
        else
        {
            if (_masterService is not null)
                ChartViewModel.UpdatePlaybackCursor(_masterService.CurrentTimestamp);
        }
    }

    /// <summary>
    /// Rebuild the left-side <see cref="Signals"/> collection from the
    /// currently loaded trace + (optional) DBC. v3.2.0 MINOR: walks
    /// <see cref="ITraceSessionRegistry.GetFrames"/> per source so multi-trace
    /// overlays see all frames across all loaded sources.
    /// </summary>
    private async Task RebuildSignalsAsync()
    {
        Signals.Clear();
        var dbc = _dbcService.Current;
        if (dbc is null)
        {
            // No DBC — nothing to decode against.
            await Task.CompletedTask;
            return;
        }

        // v3.2.0 MINOR: bucket frames from all loaded sources by CAN ID.
        var byId = new Dictionary<uint, List<ReplayFrame>>();
        foreach (var source in _registry.Sources)
        {
            foreach (var f in _registry.GetFrames(source.SourceId))
            {
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
        await Task.CompletedTask;
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
        _registry.SourcesChanged -= OnRegistrySourcesChanged;
        if (_masterService is not null)
            _masterService.FrameEmitted -= OnFrameEmitted;
        GC.SuppressFinalize(this);
    }
}