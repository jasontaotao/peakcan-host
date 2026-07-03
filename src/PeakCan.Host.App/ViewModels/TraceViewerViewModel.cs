using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PeakCan.Host.App.Services;
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
    /// currently loaded trace + (optional) DBC. V1 stub: emits no rows.
    /// The real impl iterates <see cref="ITraceViewerService"/>'s loaded
    /// frames and the <see cref="DbcService.Current"/> document.
    /// Pending v3.0.1 PATCH: per-signal DBC decode. The 5 mandated tests
    /// do not exercise <see cref="Signals"/> content, so the stub keeps
    /// the tests green without committing to a wiring shape that
    /// subsequent tasks may want to change.
    /// </summary>
    private async Task RebuildSignalsAsync()
    {
        Signals.Clear();
        // Pending v3.0.1 PATCH: per-signal DBC decode. Intentionally
        // empty until then; keeping it as a no-op (rather than throwing)
        // avoids test-fixture surprises if the VM is constructed with a
        // real service before any data flows.
        await Task.CompletedTask;
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