using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Mobile.Core.Models;
using PeakCan.Host.Mobile.Core.Platform;
using PeakCan.Host.Mobile.Core.Services;

namespace PeakCan.Host.Mobile.Core.ViewModels;

/// <summary>
/// 单 trace session 的 UI 状态机。持有一个 <see cref="IStreamingTracePlayer"/>，
/// 在 FrameEmitted（播放器线程）上收集到 pending 队列，由 50ms UI 定时器 drain
/// 到环形缓冲并通知 VisibleRows 刷新。ID 过滤在 ingest 谓词处生效；过滤变化时清空
/// 已有 ring，保证 UI 只显示过滤后的新帧。DurationText/Progress01 在 DurationScanner
/// 后台扫完后填充。
/// </summary>
public sealed partial class TraceSessionViewModel : ObservableObject, IDisposable
{
    private readonly IUiDispatcher _ui;
    private readonly IStreamingSourceFactory _sourceFactory;
    private readonly Func<IStreamingTraceSource, IStreamingTracePlayer> _playerFactory;
    private readonly ILogger _logger;

    private readonly object _emitGate = new();
    private readonly List<ReplayFrame> _pending = new();
    private readonly FrameRingBuffer _ring = new(5000);

    private IStreamingTracePlayer? _player;
    private IReadOnlySet<uint>? _idFilter;
    private double _duration;
    private bool _durationKnownValue;
    private IDisposable? _drainTimer;
    private SessionState _state = SessionState.Empty;

    public TraceSessionViewModel(IUiDispatcher ui, IStreamingSourceFactory sourceFactory,
        Func<IStreamingTraceSource, IStreamingTracePlayer> playerFactory, ILogger? logger = null)
    {
        _ui = ui;
        _sourceFactory = sourceFactory;
        _playerFactory = playerFactory;
        _logger = logger ?? NullLogger.Instance;
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
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _idFilterText;

    public IReadOnlyList<FrameRow> VisibleRows => _ring.Snapshot();

    /// <summary>Open a cached file, prefetch a display-only first screen, and start the duration scan.</summary>
    public async Task OpenAsync(string cachedFilePath, CancellationToken ct = default)
    {
        State = SessionState.Empty;
        ErrorMessage = null;
        DurationKnown = false;
        DurationText = "??:??";
        DurationScanProgress = 0;
        ClearPlaybackBuffer();

        var source = _sourceFactory.Create(cachedFilePath);
        var open = await source.OpenAsync(ct);
        int prefetched = 0;
        await foreach (var f in open.Frames.WithCancellation(ct))
        {
            _ring.Add(FrameRow.FromReplayFrame(f));
            if (++prefetched >= 200) break;
        }

        State = SessionState.Ready;
        RaiseRowsChanged();
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
                var scan = await DurationScanner.ScanAsync(fs, progress, ct).ConfigureAwait(false);
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
        lock (_emitGate)
        {
            if (PassesFilter(f)) _pending.Add(f);
        }
    }

    private bool PassesFilter(ReplayFrame f) => _idFilter is null || _idFilter.Contains(f.Id);

    private void Drain()
    {
        List<ReplayFrame> batch;
        lock (_emitGate)
        {
            if (_pending.Count == 0) return;
            batch = new List<ReplayFrame>(_pending);
            _pending.Clear();
        }

        foreach (var f in batch) _ring.Add(FrameRow.FromReplayFrame(f));
        CurrentTimeText = FormatTime(batch[^1].Timestamp);
        if (_durationKnownValue && _duration > 0) Progress01 = Math.Clamp(batch[^1].Timestamp / _duration, 0, 1);
        RaiseRowsChanged();
    }

    private void RaiseRowsChanged() => OnPropertyChanged(nameof(VisibleRows));

    private void ClearPlaybackBuffer()
    {
        lock (_emitGate) _pending.Clear();
        _ring.Clear();
    }

    private void OnPlaybackEnded(object? sender, PlaybackEndedEventArgs e)
    {
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
            if (_durationKnownValue && _duration > 0) Progress01 = Math.Clamp(p, 0, 1);
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
            ClearPlaybackBuffer();
            CurrentTimeText = "00:00:00";
            Progress01 = 0;
            RaiseRowsChanged();
        }

        _ = _player.PlayAsync();
        State = SessionState.Playing;
        _drainTimer ??= _ui.StartTimer(TimeSpan.FromMilliseconds(50), Drain);
    }

    [RelayCommand]
    private void Stop()
    {
        _player?.Stop();
        _drainTimer?.Dispose();
        _drainTimer = null;
        IsSeekBusy = false;
        ClearPlaybackBuffer();
        CurrentTimeText = "00:00:00";
        Progress01 = 0;
        State = SessionState.Ready;
        RaiseRowsChanged();
    }

    [RelayCommand]
    private void SeekTo(double timestamp)
    {
        if (_player is null || !DurationKnown) return;
        IsSeekBusy = true;
        _ = _player.SeekAsync(Math.Clamp(timestamp, 0, _duration));
    }

    partial void OnIdFilterTextChanged(string? value)
    {
        var parsed = CanIdListParser.Parse(value);
        _idFilter = parsed.AllowList;

        // 过滤变更必须满足验收语义：表格只保留匹配帧。P1 清空已有 ring，
        // 后续只 ingest 匹配帧；SQLite 全量回看在 P2 实现。
        ClearPlaybackBuffer();
        RaiseRowsChanged();
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
        ClearPlaybackBuffer();
        _drainTimer ??= _ui.StartTimer(TimeSpan.FromMilliseconds(50), Drain);
    }

    internal void PauseForBackground()
    {
        if (_player is null || State != SessionState.Playing) return;
        _player.Pause();
        State = SessionState.Paused;
    }

    private static string FormatTime(double seconds) =>
        seconds <= 0 ? "00:00:00" : TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss");

    public void Dispose()
    {
        _drainTimer?.Dispose();
        if (_player is not null)
        {
            _player.FrameEmitted -= OnFrameEmitted;
            _player.PlaybackEnded -= OnPlaybackEnded;
            _player.SeekProgress -= OnSeekProgress;
            _player.Dispose();
        }
    }
}
