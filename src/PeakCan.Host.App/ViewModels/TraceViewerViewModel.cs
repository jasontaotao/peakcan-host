using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PeakCan.Host.App.Services;
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
/// Mirrors the threading model used by <see cref="ReplayViewModel"/>:
/// capture <see cref="SynchronizationContext"/> at construction so
/// timer-thread callbacks can marshal to the UI thread; the test path
/// (no captured context) falls back to direct setters.
/// <para>
/// <b>Cursor propagation:</b> the underlying service exposes
/// <see cref="ITraceViewerService.CurrentTimestamp"/> as a polled
/// property, not via <c>PropertyChanged</c>. We piggy-back on
/// <see cref="ITraceViewerService.FrameEmitted"/>, which fires on every
/// cursor advance — the same pattern <see cref="ReplayViewModel"/>
/// uses for its cursor UI. This avoids changing the v1 contract on
/// <see cref="ITraceViewerService"/>.
/// </para>
/// </summary>
public sealed partial class TraceViewerViewModel : ObservableObject, IDisposable
{
    private readonly ITraceViewerService _service;
    private readonly DbcService _dbcService;
    private readonly ILogger<TraceViewerViewModel> _logger;
    // Mirrors ReplayViewModel: FrameEmitted fires on the timeline's
    // timer thread. Captured at construction; null in test fixtures
    // without an STA SynchronizationContext (direct set is safe there).
    private readonly SynchronizationContext? _syncContext;
    private bool _disposed;

    [ObservableProperty]
    private string _loadedTracePath = "";

    [ObservableProperty]
    private string _loadedDbcPath = "";

    [ObservableProperty]
    private double _scrubberValue;

    [ObservableProperty]
    private double _totalDuration;

    public ObservableCollection<TraceSignalRow> Signals { get; } = new();
    public TraceChartViewModel ChartViewModel { get; } = new();

    public TraceViewerViewModel(
        ITraceViewerService service,
        DbcService dbcService,
        ILogger<TraceViewerViewModel> logger)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _dbcService = dbcService ?? throw new ArgumentNullException(nameof(dbcService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _syncContext = SynchronizationContext.Current;
        _service.FrameEmitted += OnFrameEmitted;
    }

    /// <summary>
    /// Load an ASC recording into the trace service. Updates
    /// <see cref="LoadedTracePath"/>, <see cref="TotalDuration"/>, and
    /// forwards the duration to <see cref="TraceChartViewModel"/> so the
    /// X-axis range matches. Surfaces <see cref="ReplayLoadException"/>
    /// to the VM caller (window shows MessageBox); other exception
    /// types propagate unhandled.
    /// </summary>
    [RelayCommand]
    public async Task OpenFileAsync(string path)
    {
        try
        {
            await _service.LoadAsync(path).ConfigureAwait(true);
            LoadedTracePath = path;
            TotalDuration = _service.TotalDuration;
            ChartViewModel.SetTotalDuration(_service.TotalDuration);
            await RebuildSignalsAsync().ConfigureAwait(true);
        }
        catch (ReplayException ex)
        {
            LogLoadFailed(_logger, ex, path);
            throw;  // VM caller shows MessageBox
        }
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

    [RelayCommand]
    public void Play() => _service.Play();

    [RelayCommand]
    public void Pause() => _service.Pause();

    [RelayCommand]
    public void Stop()
    {
        _service.Stop();
        ScrubberValue = 0;
    }

    [RelayCommand]
    public void SeekTo(double t) => _service.Seek(t);

    partial void OnScrubberValueChanged(double value)
    {
        if (TotalDuration > 0) _service.Seek(value);
    }

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
                ChartViewModel.UpdatePlaybackCursor(_service.CurrentTimestamp);
            }, null);
        }
        else
        {
            ChartViewModel.UpdatePlaybackCursor(_service.CurrentTimestamp);
        }
    }

    /// <summary>
    /// Rebuild the left-side <see cref="Signals"/> collection from the
    /// currently loaded trace + (optional) DBC. For every message in the
    /// loaded DBC, every signal on that message that has at least one
    /// matching frame in <see cref="ITraceViewerService.LoadedFrames"/>
    /// becomes one <see cref="TraceSignalRow"/>, with <c>LatestValue</c>
    /// set to the engineering value decoded from the LAST matching frame
    /// (matches the v3.0.0 release-notes promise: the most recent value
    /// at load time). Rows are sorted by <c>(CanIdHex, SignalName)</c>
    /// for deterministic output.
    /// <para>
    /// When <see cref="DbcService.Current"/> is null (no DBC loaded) the
    /// collection is left empty; same when a DBC is loaded but no
    /// <see cref="ITraceViewerService.LoadedFrames"/> entries match any
    /// of its messages. Runs synchronously on the UI thread — the
    /// caller (OpenFileAsync / LoadDbcAsync) is already on the UI
    /// thread, the work is bounded by <c>|messages| * |signals|</c>
    /// which is small for typical DBC sizes, and the result is bound
    /// to an <see cref="ObservableCollection{T}"/> which would require
    /// a marshal-back if we ever moved it off-thread.
    /// </para>
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

        var frames = _service.LoadedFrames;
        // Bucket frames by CAN ID once (linear in |frames|); then walk
        // DBC messages and emit one row per signal that has at least
        // one matching frame.
        var byId = new Dictionary<uint, List<ReplayFrame>>(frames.Count);
        foreach (var f in frames)
        {
            if (!byId.TryGetValue(f.Id, out var list))
            {
                list = new List<ReplayFrame>();
                byId[f.Id] = list;
            }
            list.Add(f);
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
    /// Unsubscribe from the service and stop playback. Safe to call
    /// multiple times — <c>_disposed</c> guards re-entry.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _service.FrameEmitted -= OnFrameEmitted;
        GC.SuppressFinalize(this);
    }
}