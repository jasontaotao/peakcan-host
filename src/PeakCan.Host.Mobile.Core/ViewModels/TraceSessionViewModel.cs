using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.Core.Replay;
using PeakCan.HIL.Core.Dbc;
using PeakCan.Host.Mobile.Core.Models;
using PeakCan.Host.Mobile.Core.Platform;
using PeakCan.Host.Mobile.Core.Services;

namespace PeakCan.Host.Mobile.Core.ViewModels;

/// <summary>
/// 单 trace session 的 UI 状态机。播放器线程写入有界 pending 队列；UI 定时器批量
/// drain 到固定容量 FrameRingBuffer，并把最近 viewport 行写入稳定行槽。行槽用
/// INPC in-place 更新，避免高频 CollectionView Insert/Remove/Reset 造成 native 膨胀。
/// </summary>
public sealed partial class TraceSessionViewModel : ObservableObject, IDisposable
{
    private const int MaxPendingFrames = 10_000;
    private const int ViewportRowCount = 80;
    private static readonly TimeSpan UiDrainInterval = TimeSpan.FromMilliseconds(100);

    private readonly IUiDispatcher _ui;
    private readonly IStreamingSourceFactory _sourceFactory;
    private readonly Func<IStreamingTraceSource, IStreamingTracePlayer> _playerFactory;
    private readonly ILogger _logger;
    private readonly ITraceCacheSinkFactory? _cacheSinkFactory;
    private readonly object _cacheSinkGate = new();

    private readonly object _emitGate = new();
    private readonly Queue<ReplayFrame> _pending = new();
    private readonly FrameRingBuffer _rows = new(5000);
    private readonly FrameRowSlot[] _viewport = new FrameRowSlot[ViewportRowCount];
    private readonly FrameRow[] _viewportSource = new FrameRow[ViewportRowCount];

    private IStreamingTracePlayer? _player;
    private IReadOnlySet<uint>? _idFilter;
    private double _duration;
    private bool _durationKnownValue;
    private IDisposable? _drainTimer;
    private SessionState _state = SessionState.Empty;
    private FrameRow? _latestVisibleRow;
    private ITraceCacheSink? _cacheSink;
    private long? _traceId;
    private DbcCatalog? _dbc;
    private string? _cachedFilePath;
    private CancellationTokenSource? _chartBackfillCts;
    private Task? _chartBackfillTask;
    private readonly TraceChartViewModel _chart;

    public TraceSessionViewModel(IUiDispatcher ui, IStreamingSourceFactory sourceFactory,
        Func<IStreamingTraceSource, IStreamingTracePlayer> playerFactory,
        ILogger? logger = null, ITraceCacheSinkFactory? cacheSinkFactory = null)
    {
        _ui = ui;
        _sourceFactory = sourceFactory;
        _playerFactory = playerFactory;
        _logger = logger ?? NullLogger.Instance;
        _cacheSinkFactory = cacheSinkFactory;
        _chart = new TraceChartViewModel(null, ui);
        _chart.SignalSelected += (_, _) => _ = BackfillSelectedSignalsAsync();
        for (var i = 0; i < ViewportRowCount; i++)
            _viewport[i] = new FrameRowSlot();
    }

    public SessionState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            OnPropertyChanged();
            PlayPauseLabel = value == SessionState.Playing ? "⏸" : "▶";
        }
    }

    [ObservableProperty] private string _currentTimeText = "00:00:00";
    [ObservableProperty] private string _durationText = "??:??";
    [ObservableProperty] private double _durationScanProgress;
    [ObservableProperty] private bool _durationKnown;
    [ObservableProperty] private string _playPauseLabel = "▶";
    [ObservableProperty] private double _progress01;
    [ObservableProperty] private bool _isSeekBusy;
    [ObservableProperty] private bool _isSeekDragging;
    [ObservableProperty] private string _seekProgressText = string.Empty;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _idFilterText;
    [ObservableProperty] private string _cacheStatusText = string.Empty;
    [ObservableProperty] private string _skippedLinesText = string.Empty;
    [ObservableProperty] private string _dbcStatusText = "未加载 DBC";

    public long? TraceId => _traceId;

    /// <summary>Stable fixed-size slots; row values are updated in place.</summary>
    public IReadOnlyList<FrameRowSlot> VisibleRows => _viewport;

    public FrameRow? LatestVisibleRow => _latestVisibleRow;

    public DbcCatalog? Dbc => _dbc;

    /// <summary>Chart-side selected signal state.</summary>
    public TraceChartViewModel Chart => _chart;

    /// <summary>Gets the active chart backfill task for deterministic test synchronization.</summary>
    internal Task? ChartBackfillTask => _chartBackfillTask;

    /// <summary>Sets the catalog used for subsequently decoded rows and clears stale summaries.</summary>
    public void SetDbc(DbcCatalog? catalog)
    {
        _dbc = catalog;
        DbcStatusText = catalog is null ? "未加载 DBC" : $"DBC: {catalog.SourceName}";
        _chart.SetCatalog(catalog);
        ClearPlaybackBuffer(restartChartBackfill: true);
    }

    /// <summary>Open a cached file, prefetch a display-only first screen, and start the duration scan.</summary>
    public Task OpenAsync(string cachedFilePath, CancellationToken ct = default)
    {
        var info = new FileInfo(cachedFilePath);
        return OpenAsync(cachedFilePath, info.Name, info.Length, ct);
    }

    public async Task OpenAsync(string cachedFilePath, string sourceName, long fileSizeBytes, CancellationToken ct = default)
    {
        State = SessionState.Empty;
        ErrorMessage = null;
        DurationKnown = false;
        DurationText = "??:??";
        DurationScanProgress = 0;
        _traceId = null;
        lock (_cacheSinkGate) _cacheSink = null;
        CacheStatusText = string.Empty;
        _cachedFilePath = cachedFilePath;
        ClearPlaybackBuffer();

        if (_cacheSinkFactory is not null)
        {
            var sink = await _cacheSinkFactory.StartAsync(sourceName, fileSizeBytes, ct).ConfigureAwait(false);
            _traceId = sink?.TraceId;
            lock (_cacheSinkGate) _cacheSink = sink;
            if (sink is null)
                _ui.Post(() => CacheStatusText = "缓存不可用");
        }

        var source = _sourceFactory.Create(cachedFilePath);
        await using var open = await source.OpenAsync(ct: ct).ConfigureAwait(false);
        var prefetched = 0;
        await foreach (var f in open.Frames.WithCancellation(ct))
        {
            _rows.Add(FrameRow.FromReplayFrame(f, _dbc));
            if (++prefetched >= 200) break;
        }

        UpdateViewport();
        State = SessionState.Ready;
        StartChartBackfill();
        _player = _playerFactory(source);
        _player.FrameEmitted += OnFrameEmitted;
        _player.PlaybackEnded += OnPlaybackEnded;
        _player.SeekProgress += OnSeekProgress;

        var progress = new Progress<double>(p => _ui.Post(() => DurationScanProgress = p));
        _ = Task.Run(async () =>
        {
            try
            {
                await using var fs = new FileStream(cachedFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var scan = cachedFilePath.EndsWith(".blf", StringComparison.OrdinalIgnoreCase)
                    ? await BlfDurationScanner.ScanAsync(fs, progress, ct).ConfigureAwait(false)
                    : await DurationScanner.ScanAsync(fs, progress, ct).ConfigureAwait(false);
                _duration = scan.DurationSeconds;
                _durationKnownValue = true;
                _ui.Post(() =>
                {
                    DurationKnown = true;
                    DurationText = FormatTime(_duration);
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "duration scan failed");
            }
        }, ct);
    }

    private void OnFrameEmitted(ReplayFrame f)
    {
        ITraceCacheSink? cacheSink;
        lock (_cacheSinkGate) cacheSink = _cacheSink;
        cacheSink?.Enqueue(f);

        if (PassesFilter(f)) _chart.Ingest(f);

        lock (_emitGate)
        {
            if (!PassesFilter(f)) return;
            if (_pending.Count == MaxPendingFrames)
                _pending.Dequeue(); // UI starvation guard; player FramesEmitted remains authoritative.
            _pending.Enqueue(f);
        }
    }

    private bool PassesFilter(ReplayFrame f) => _idFilter is null || _idFilter.Contains(f.Id);

    private void Drain()
    {
        List<FrameRow> batch;
        lock (_emitGate)
        {
            if (_pending.Count == 0) return;
            batch = new List<FrameRow>(_pending.Count);
            while (_pending.Count > 0)
                batch.Add(FrameRow.FromReplayFrame(_pending.Dequeue(), _dbc));
        }

        foreach (var row in batch) _rows.Add(row);
        CurrentTimeText = FormatTime(batch[^1].Timestamp);
        if (!IsSeekDragging && _durationKnownValue && _duration > 0) Progress01 = Math.Clamp(batch[^1].Timestamp / _duration, 0, 1);
        SkippedLinesText = _player?.SkippedLines > 0 ? $"已跳过 {_player.SkippedLines} 行" : string.Empty;
        _chart.UpdateCursor(batch[^1].Timestamp);
        _chart.RefreshRender();
        UpdateViewport();
    }

    private void UpdateViewport()
    {
        var count = _rows.CopyLatest(_viewportSource);
        var blankCount = ViewportRowCount - count;

        for (var i = 0; i < blankCount; i++)
            _viewport[i].Clear();

        for (var i = 0; i < count; i++)
            _viewport[blankCount + i].UpdateFrom(_viewportSource[i]);

        _latestVisibleRow = count == 0 ? null : _viewportSource[count - 1];
    }

    /// <summary>Backfills selected signal history from the source file so a late
    /// selection still shows the complete trace instead of only future frames.</summary>
    private void StartChartBackfill()
    {
        if (_cachedFilePath is null || _dbc is null || _chart.SelectedSignals.Count == 0)
            return;

        _chartBackfillTask = BackfillSelectedSignalsAsync();
    }

    internal async Task BackfillSelectedSignalsAsync()
    {
        var path = _cachedFilePath;
        var dbc = _dbc;
        if (path is null || dbc is null || _chart.SelectedSignals.Count == 0)
            return;

        _chartBackfillCts?.Cancel();
        _chartBackfillCts?.Dispose();
        _chartBackfillCts = new CancellationTokenSource();
        var ct = _chartBackfillCts.Token;

        try
        {
            var source = _sourceFactory.Create(path);
            await using var open = await source.OpenAsync(ct: ct).ConfigureAwait(false);
            var samplesBySignal = _chart.SelectedSignals
                .ToDictionary(i => i.Key, _ => new List<SignalSample>());

            await foreach (var frame in open.Frames.WithCancellation(ct).ConfigureAwait(false))
            {
                if (!PassesFilter(frame)) continue;
                var message = dbc.FindMessage(frame.Id, frame.IsExtended);
                if (message is null) continue;

                var payload = frame.Data.AsSpan(0, Math.Min(frame.Dlc, frame.Data.Length));
                foreach (var selection in _chart.SelectedSignals)
                {
                    if (selection.Key.CanId != frame.Id || selection.Key.IsExtended != frame.IsExtended)
                        continue;

                    var signal = message.Signals.FirstOrDefault(s => s.Name == selection.Key.SignalName);
                    if (signal is null || !DbcCatalog.IsSignalActive(message, signal, payload))
                        continue;

                    samplesBySignal[selection.Key]
                        .Add(new SignalSample(frame.Timestamp, SignalDecoder.Decode(payload, signal)));
                }
            }

            foreach (var (key, samples) in samplesBySignal)
                _chart.ReplaceSamples(key, samples);
        }
        catch (OperationCanceledException)
        {
            // Selection, filtering, playback, or disposal replaced the backfill.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "chart history backfill failed");
        }
    }
    private void ClearPlaybackBuffer(bool restartChartBackfill = false)
    {
        _chartBackfillCts?.Cancel();
        lock (_emitGate) _pending.Clear();
        _rows.Clear();
        SkippedLinesText = string.Empty;
        _chart.Clear();
        UpdateViewport();

        if (restartChartBackfill)
            StartChartBackfill();
    }

    private void OnPlaybackEnded(object? sender, PlaybackEndedEventArgs e)
    {
        ITraceCacheSink? sink;
        lock (_cacheSinkGate)
        {
            sink = _cacheSink;
            _cacheSink = null;
        }

        _ = Task.Run(async () =>
        {
            if (sink is not null)
            {
                try
                {
                    var cacheComplete = await sink.CloseAsync(e.Error is null).ConfigureAwait(false);
                    _ui.Post(() => CacheStatusText = cacheComplete ? "缓存完成" : "缓存未完成");
                }
                catch
                {
                    _ui.Post(() => CacheStatusText = "缓存已停用");
                }
            }
        });

        _ui.Post(() =>
        {
            IsSeekBusy = false;
            if (e.Error is null)
                State = SessionState.Ended;
            else
            {
                State = SessionState.Failed;
                ErrorMessage = e.Error.Message;
            }
            _drainTimer?.Dispose();
            _drainTimer = null;
            Drain();
        });
    }

    private void OnSeekProgress(double p)
    {
        _ui.Post(() =>
        {
            IsSeekBusy = p < 1.0;
            SeekProgressText = p < 1.0 ? $"快进 {p:P0}..." : string.Empty;
            // 不覆盖 Progress01：快进扫描进度 ≠ 播放位置；
            // seek 完成后 Drain 基于实际帧更新 Progress01
        });
    }

    [RelayCommand]
    private void TogglePlay()
    {
        if (_player is null) return;

        if (State == SessionState.Playing)
        {
            _player.Pause();
            State = SessionState.Paused;
            return;
        }

        // 预读首屏仅用于打开后 preview；正式播放从头开始，避免重复 ingest。
        if (State is SessionState.Ready or SessionState.Ended or SessionState.Failed)
        {
            ClearPlaybackBuffer(restartChartBackfill: true);
            // 保留 seek 位置，不重置 Progress01/CurrentTimeText
        }

        _ = _player.PlayAsync();
        State = SessionState.Playing;
        _drainTimer ??= _ui.StartTimer(UiDrainInterval, Drain);
    }

    [RelayCommand]
    private void Stop()
    {
        _player?.Stop();
        _drainTimer?.Dispose();
        _drainTimer = null;
        IsSeekBusy = false;
        ClearPlaybackBuffer(restartChartBackfill: true);
        CurrentTimeText = "00:00:00";
        Progress01 = 0;
        State = SessionState.Ready;
    }

    [RelayCommand]
    private void SeekTo(double fraction)
    {
        if (_player is null || !DurationKnown) return;
        IsSeekBusy = true;
        IsSeekDragging = false;
        SeekProgressText = "快进...";
        var ts = Math.Clamp(fraction, 0, 1) * _duration;
        _ = _player.SeekAsync(ts);
        CurrentTimeText = FormatTime(ts);
        // Stopped 状态下 seek 不 emit 帧；清空旧数据让用户知道位置已变
        if (State == SessionState.Ready) ClearPlaybackBuffer(restartChartBackfill: true);
    }

    partial void OnIdFilterTextChanged(string? value)
    {
        var parsed = CanIdListParser.Parse(value);
        _idFilter = parsed.AllowList;

        // 过滤变更必须满足验收语义：viewport 只保留匹配帧。P1 清空已有 rows，
        // 后续只 ingest 匹配帧；SQLite 全量回看在 P2 实现。
        ClearPlaybackBuffer(restartChartBackfill: true);
    }

    /// <summary>Forward playback speed multiplier to the active player.</summary>
    public void SetSpeed(double multiplier) => _player?.SetSpeed(multiplier);

    /// <summary>设置 ID 过滤（十六进制，逗号分隔；空串=清除）。</summary>
    public void SetIdFilter(string text) { IdFilterText = string.IsNullOrWhiteSpace(text) ? null : text; }

    internal void MarkReadyForEmit(IStreamingTracePlayer player)
    {
        _player = player;
        _player.FrameEmitted += OnFrameEmitted;
        _player.PlaybackEnded += OnPlaybackEnded;
        _player.SeekProgress += OnSeekProgress;
        State = SessionState.Playing;
        ClearPlaybackBuffer(restartChartBackfill: true);
        _drainTimer ??= _ui.StartTimer(UiDrainInterval, Drain);
    }

    public void PauseForBackground()
    {
        if (_player is null || State != SessionState.Playing) return;
        _player.Pause();
        State = SessionState.Paused;
    }

    private static string FormatTime(double seconds) =>
        seconds <= 0 ? "00:00:00" : TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss");

    public void Dispose()
    {
        _chartBackfillCts?.Cancel();
        _drainTimer?.Dispose();
        if (_player is not null)
        {
            _player.FrameEmitted -= OnFrameEmitted;
            _player.PlaybackEnded -= OnPlaybackEnded;
            _player.SeekProgress -= OnSeekProgress;
            _player.Dispose();
        }

        ITraceCacheSink? sink;
        lock (_cacheSinkGate)
        {
            sink = _cacheSink;
            _cacheSink = null;
        }

        sink?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}











