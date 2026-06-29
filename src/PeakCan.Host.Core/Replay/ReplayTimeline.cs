using System.Threading;

namespace PeakCan.Host.Core.Replay;

/// <summary>
/// Timer-driven playback scheduler. Internal — used only by
/// <see cref="ReplayService"/>. Owns the playback timer; mutates
/// state on <see cref="Play"/>/<see cref="Pause"/>/<see cref="Resume"/>/
/// <see cref="Seek"/>/<see cref="SetSpeed"/>/<see cref="Stop"/>.
/// </summary>
internal sealed class ReplayTimeline
{
    private readonly object _lock = new();
    private readonly Action<ReplayFrame> _emit;
    private readonly Action? _onPlaybackEnded;
    private IReadOnlyList<ReplayFrame> _frames = Array.Empty<ReplayFrame>();
    private int _nextFrameIndex;
    private double _currentTimestamp;
    private double _speed = 1.0;
    private bool _isPlaying;
    private bool _hasStarted; // true once Play() has been called with frames loaded; resets only via Stop()
    private bool _loop;
    private DateTime _playStartWallClock;
    private double _playStartTimestamp;
    private Timer? _timer;

    public ReplayTimeline(Action<ReplayFrame> emit, Action? onPlaybackEnded = null)
    {
        _emit = emit;
        _onPlaybackEnded = onPlaybackEnded;
    }

    public double CurrentTimestamp { get { lock (_lock) return _currentTimestamp; } }
    public double Speed { get { lock (_lock) return _speed; } }
    public bool IsPlaying { get { lock (_lock) return _isPlaying; } }

    /// <summary>
    /// If true, playback restarts from t=0 upon reaching the last frame's
    /// timestamp. If false (default), playback auto-stops and the
    /// <c>onPlaybackEnded</c> ctor callback is raised exactly once.
    /// </summary>
    public bool Loop
    {
        get { lock (_lock) return _loop; }
        set { lock (_lock) _loop = value; }
    }

    /// <summary>
    /// True once <see cref="Play"/> has been called with frames loaded, since
    /// the last <see cref="Stop"/>. Used by <see cref="ReplayService"/> to
    /// distinguish Stopped (no frames or never played) from Paused (was
    /// playing, now halted). Play() on an empty timeline leaves this false
    /// — there is nothing to play back, so the state stays Stopped.
    /// </summary>
    public bool HasStarted { get { lock (_lock) return _hasStarted; } }

    public void SetFrames(IReadOnlyList<ReplayFrame> frames)
    {
        lock (_lock)
        {
            _frames = frames;
            _nextFrameIndex = 0;
            _currentTimestamp = 0.0;
        }
    }

    public void Play()
    {
        lock (_lock)
        {
            if (_isPlaying) return;
            if (_frames.Count == 0) return; // nothing to play; leave state as Stopped
            _playStartWallClock = DateTime.UtcNow;
            _playStartTimestamp = _currentTimestamp;
            _isPlaying = true;
            _hasStarted = true;
            _timer ??= new Timer(OnTick, null, dueTime: 1, period: 1);
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (!_isPlaying) return;
            _isPlaying = false;
        }
    }

    public void Seek(double timestamp)
    {
        lock (_lock)
        {
            _currentTimestamp = timestamp;
            _playStartTimestamp = timestamp;
            _playStartWallClock = DateTime.UtcNow;
            // Advance next-frame index to first frame with Timestamp >= target
            _nextFrameIndex = 0;
            while (_nextFrameIndex < _frames.Count && _frames[_nextFrameIndex].Timestamp < timestamp)
            {
                _nextFrameIndex++;
            }
        }
    }

    public void SetSpeed(double multiplier)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(multiplier);
        lock (_lock)
        {
            // Re-anchor play start to preserve current playback position at new speed
            _currentTimestamp = PlayedTimestamp;
            _playStartTimestamp = _currentTimestamp;
            _playStartWallClock = DateTime.UtcNow;
            _speed = multiplier;
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _isPlaying = false;
            _hasStarted = false;
            _currentTimestamp = 0.0;
            _nextFrameIndex = 0;
            _playStartTimestamp = 0.0;
        }
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>
    /// Wall-clock-adjusted timestamp of the current playback position.
    /// = play_start_timestamp + (now - play_start_wall_clock) * speed.
    /// </summary>
    private double PlayedTimestamp
    {
        get
        {
            var elapsed = (DateTime.UtcNow - _playStartWallClock).TotalSeconds * _speed;
            return _playStartTimestamp + elapsed;
        }
    }

    private void OnTick(object? state)
    {
        List<ReplayFrame>? toEmit = null;
        bool endReached = false;
        lock (_lock)
        {
            if (!_isPlaying) return;
            var now = PlayedTimestamp;
            while (_nextFrameIndex < _frames.Count && _frames[_nextFrameIndex].Timestamp <= now)
            {
                toEmit ??= new List<ReplayFrame>();
                toEmit.Add(_frames[_nextFrameIndex]);
                _currentTimestamp = _frames[_nextFrameIndex].Timestamp;
                _nextFrameIndex++;
            }
            // Detect EOF this tick: cursor walked off the end while still playing.
            // Loop=true → rewind to t=0 and continue. Loop=false → stop and raise
            // the playback-ended callback exactly once.
            if (_nextFrameIndex >= _frames.Count && _frames.Count > 0)
            {
                if (_loop)
                {
                    _nextFrameIndex = 0;
                    _currentTimestamp = 0.0;
                    _playStartTimestamp = 0.0;
                    _playStartWallClock = DateTime.UtcNow;
                }
                else
                {
                    _isPlaying = false;
                    endReached = true;
                }
            }
        }
        if (toEmit != null)
        {
            foreach (var frame in toEmit)
            {
                try { _emit(frame); }
                catch { /* sink errors must not stall playback */ }
            }
        }
        // Raise callback OUTSIDE the lock so subscribers can call back into the
        // service without deadlocking.
        if (endReached) _onPlaybackEnded?.Invoke();
    }
}
