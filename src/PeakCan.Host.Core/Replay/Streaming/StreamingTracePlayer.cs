using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PeakCan.Host.Core.Replay;

/// <summary>
/// Streaming playback engine. Pulls frames from <see cref="IStreamingTraceSource"/>,
/// paces each by its timestamp × <see cref="Speed"/> using <see cref="IReplayClock"/>.
/// A bounded channel decouples parsing from pacing and provides backpressure.
/// <see cref="Pause"/> blocks the run loop on a gate; <see cref="Resume"/> releases
/// it and re-anchors the clock so resumed playback does not burst-emit.
/// </summary>
public sealed class StreamingTracePlayer : IStreamingTracePlayer
{
    private readonly IStreamingTraceSource _source;
    private readonly IReplayClock _clock;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _pauseGate = new(1, 1);
    private readonly object _lifecycleLock = new();

    private CancellationTokenSource? _runCts;
    private double _speed = 1.0;
    private double _currentTimestamp;
    private double? _seekTarget;
    private bool _reanchorRequested = true;
    private ReplayState _state = ReplayState.Stopped;
    private long _framesEmitted;
    private bool _disposed;

    public StreamingTracePlayer(IStreamingTraceSource source, IReplayClock? clock = null, ILogger? logger = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _clock = clock ?? new WallClockReplayClock();
        _logger = logger ?? NullLogger.Instance;
    }

    public ReplayState State { get { lock (_lifecycleLock) return _state; } }
    public double CurrentTimestamp { get { lock (_lifecycleLock) return _currentTimestamp; } }
    public double Speed { get { lock (_lifecycleLock) return _speed; } }
    public long FramesEmitted => Interlocked.Read(ref _framesEmitted);

    public event Action<ReplayFrame>? FrameEmitted;
    public event EventHandler<PlaybackEndedEventArgs>? PlaybackEnded;
    public event Action<double>? SeekProgress;

    public async Task PlayAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lifecycleLock)
        {
            if (_state == ReplayState.Playing) return;
            if (_state == ReplayState.Paused) { ResumeImpl(); return; }
            _state = ReplayState.Playing;
            _reanchorRequested = true;
        }

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                double startFrom;
                lock (_lifecycleLock)
                {
                    _runCts = runCts;
                    startFrom = _seekTarget ?? _currentTimestamp;
                    _seekTarget = null;
                }

                var outcome = await RunLoopAsync(startFrom, runCts.Token).ConfigureAwait(false);
                lock (_lifecycleLock) _runCts = null;

                switch (outcome.Kind)
                {
                    case RunOutcomeKind.Eof:
                        _state = ReplayState.Stopped;
                        PlaybackEnded?.Invoke(this, new PlaybackEndedEventArgs());
                        return;
                    case RunOutcomeKind.Failed:
                        _state = ReplayState.Stopped;
                        PlaybackEnded?.Invoke(this, new PlaybackEndedEventArgs(outcome.Error));
                        return;
                    case RunOutcomeKind.SeekRequested:
                        continue;
                    case RunOutcomeKind.Stopped:
                    default:
                        _state = ReplayState.Stopped;
                        _currentTimestamp = 0;
                        return;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            lock (_lifecycleLock) _state = ReplayState.Stopped;
        }
        finally
        {
            lock (_lifecycleLock)
            {
                if (_state == ReplayState.Playing) _state = ReplayState.Stopped;
            }
        }
    }

    public void Pause()
    {
        lock (_lifecycleLock)
        {
            if (_state != ReplayState.Playing) return;
            _state = ReplayState.Paused;
        }
        _pauseGate.Wait(); // 取走 gate；run loop 在下一帧阻塞
    }

    public void Resume()
    {
        lock (_lifecycleLock)
        {
            if (_state != ReplayState.Paused) return;
            _state = ReplayState.Playing;
            _reanchorRequested = true;
        }
        _pauseGate.Release();
    }

    private void ResumeImpl()
    {
        _state = ReplayState.Playing;
        _reanchorRequested = true;
        _pauseGate.Release();
    }

    public void SetSpeed(double multiplier)
    {
        if (double.IsNaN(multiplier)) throw new ArgumentOutOfRangeException(nameof(multiplier));
        lock (_lifecycleLock)
        {
            _speed = Math.Clamp(multiplier, 0.1, 100.0);
            _reanchorRequested = true;
        }
    }

    public Task SeekAsync(double timestamp, CancellationToken ct = default)
    {
        lock (_lifecycleLock)
        {
            if (_state == ReplayState.Stopped)
            {
                _currentTimestamp = Math.Max(0, timestamp);
                _seekTarget = null;
                return Task.CompletedTask;
            }
            _seekTarget = Math.Max(0, timestamp);
        }
        _runCts?.Cancel();
        return Task.CompletedTask;
    }

    public void Stop()
    {
        lock (_lifecycleLock)
        {
            if (_state == ReplayState.Stopped) return;
            _state = ReplayState.Stopped;
            _seekTarget = null;
        }
        _runCts?.Cancel();
    }

    private async Task<RunOutcome> RunLoopAsync(double startFrom, CancellationToken ct)
    {
        StreamingTraceOpenResult session;
        try
        {
            session = await _source.OpenAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return RunOutcome.SeekOrCancel(_seekTarget);
        }
        catch (Exception ex)
        {
            return RunOutcome.Failed(ex);
        }

        await using var ownedSession = session;
        var channel = Channel.CreateBounded<ReplayFrame>(new BoundedChannelOptions(8192)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var pumpTask = PumpFramesAsync(session, channel.Writer, ct);
        var anchored = false;
        bool fastForwarding = startFrom > 0;
        DateTime anchorClock = default;
        double anchorTs = 0;

        try
        {
            await foreach (var frame in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (fastForwarding)
                {
                    if (frame.Timestamp < startFrom)
                    {
                        if (session.SourceLengthBytes is > 0)
                            SeekProgress?.Invoke((double)session.Stats.BytesRead / session.SourceLengthBytes.Value);
                        continue;
                    }
                    fastForwarding = false;
                    _reanchorRequested = true;
                    SeekProgress?.Invoke(1.0);
                }

                await _pauseGate.WaitAsync(ct).ConfigureAwait(false);
                _pauseGate.Release();

                lock (_lifecycleLock)
                {
                    if (_reanchorRequested || !anchored)
                    {
                        anchorClock = _clock.Now;
                        anchorTs = frame.Timestamp;
                        anchored = true;
                        _reanchorRequested = false;
                    }
                }

                var due = anchorClock + TimeSpan.FromSeconds((frame.Timestamp - anchorTs) / _speed);
                var remaining = due - _clock.Now;
                if (remaining > TimeSpan.Zero)
                    await _clock.Delay(remaining, ct).ConfigureAwait(false);

                lock (_lifecycleLock) _currentTimestamp = frame.Timestamp;
                Interlocked.Increment(ref _framesEmitted);
                FrameEmitted?.Invoke(frame);
            }

            var pumpError = await pumpTask.ConfigureAwait(false);
            if (pumpError is not null) throw pumpError;
            if (fastForwarding) SeekProgress?.Invoke(1.0);
            return RunOutcome.Eof;
        }
        catch (OperationCanceledException)
        {
            if (_seekTarget.HasValue) return RunOutcome.SeekOrCancel(_seekTarget);
            return RunOutcome.Stopped;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Streaming replay failed");
            return RunOutcome.Failed(ex);
        }
    }

    private static async Task<Exception?> PumpFramesAsync(
        StreamingTraceOpenResult session,
        ChannelWriter<ReplayFrame> writer,
        CancellationToken ct)
    {
        try
        {
            await foreach (var frame in session.Frames.WithCancellation(ct).ConfigureAwait(false))
                await writer.WriteAsync(frame, ct).ConfigureAwait(false);
            writer.TryComplete();
            return null;
        }
        catch (OperationCanceledException)
        {
            writer.TryComplete(new OperationCanceledException(ct));
            return null;
        }
        catch (Exception ex)
        {
            writer.TryComplete(ex);
            return ex;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private readonly record struct RunOutcome(RunOutcomeKind Kind, Exception? Error, double? Seek)
    {
        public static readonly RunOutcome Eof = new(RunOutcomeKind.Eof, null, null);
        public static readonly RunOutcome Stopped = new(RunOutcomeKind.Stopped, null, null);
        public static RunOutcome Failed(Exception ex) => new(RunOutcomeKind.Failed, ex, null);
        public static RunOutcome SeekOrCancel(double? seek) => new(RunOutcomeKind.SeekRequested, null, seek);
    }

    private enum RunOutcomeKind { Eof, Stopped, Failed, SeekRequested }
}

