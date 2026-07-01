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
    private readonly Action<PlaybackEndedEventArgs>? _onPlaybackEnded;
    private readonly Action<Exception>? _onSinkThrew;
    private IReadOnlyList<ReplayFrame> _frames = Array.Empty<ReplayFrame>();
    private int _nextFrameIndex;
    private double _currentTimestamp;
    private double _speed = 1.0;
    private bool _isPlaying;
    private bool _hasStarted; // true once Play() has been called with frames loaded; resets only via Stop()
    private bool _loop;
    // v1.5.1 PATCH Task 2: inclusive timestamp window applied at the
    // OnTick iteration boundary. null = unbounded on that side. Persist
    // across SetFrames (range is a user choice about the timeline, not
    // the loaded file) — the VM clears via OpenAsync when a new file is
    // loaded because a new file's timestamps likely differ.
    private double? _startTimestamp;
    private double? _endTimestamp;
    private DateTime _playStartWallClock;
    private double _playStartTimestamp;
    private Timer? _timer;
    // v1.4.2 PATCH Item 3: captured first sink exception for surfacing via
    // PlaybackEndedEventArgs.Error. Set once; subsequent sink throws are
    // logged but not propagated (first-failure wins per spec Decision 5).
    private Exception? _sinkException;

    public ReplayTimeline(
        Action<ReplayFrame> emit,
        Action<PlaybackEndedEventArgs>? onPlaybackEnded = null,
        Action<Exception>? onSinkThrew = null)
    {
        _emit = emit;
        _onPlaybackEnded = onPlaybackEnded;
        _onSinkThrew = onSinkThrew;
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
    /// v1.5.1 PATCH Task 2: inclusive lower bound on emitted frames'
    /// timestamp. <c>null</c> = unbounded below. Range filter is enforced
    /// at the OnTick iteration boundary (composed with the existing
    /// <c>frame.Timestamp &lt;= now</c> predicate), NOT at the emit
    /// boundary — the cursor skips frames before Start. Re-applied after
    /// a Loop rewind. Thread-safe via the timeline's internal lock.
    /// </summary>
    public double? StartTimestamp
    {
        get { lock (_lock) return _startTimestamp; }
        set { lock (_lock) _startTimestamp = value; }
    }

    /// <summary>
    /// v1.5.1 PATCH Task 2: inclusive upper bound on emitted frames'
    /// timestamp. <c>null</c> = unbounded above. Same composition +
    /// re-application semantics as <see cref="StartTimestamp"/>.
    /// </summary>
    public double? EndTimestamp
    {
        get { lock (_lock) return _endTimestamp; }
        set { lock (_lock) _endTimestamp = value; }
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
            // v1.5.1 PATCH Task 2: range filter at the iteration boundary.
            // null on either side means unbounded on that side. The filter
            // is composed with the existing timestamp predicate so the
            // cursor only walks across in-range frames; CanIdFilter is
            // applied later at the emit boundary by ReplayService.
            var startOrMin = _startTimestamp ?? double.NegativeInfinity;
            var endOrMax = _endTimestamp ?? double.PositiveInfinity;
            // Pre-skip: if the cursor sits in the "before start" region,
            // walk _nextFrameIndex forward past every out-of-range frame
            // WITHOUT emitting. The main while-loop predicate only fires
            // for in-range frames; without this pre-skip, the cursor
            // would stay stuck on the first out-of-range frame because
            // the while-loop body never runs to advance _nextFrameIndex.
            // After the pre-skip, the existing while-loop runs as before
            // — the cursor's wall-clock "now" naturally catches up to
            // the first in-range frame's Timestamp over time.
            //
            // Above-bound case (cursor past endOrMax, e.g. Seek past End):
            // not pre-skipped here — the main while-loop predicate filters
            // such frames out (Timestamp <= endOrMax fails), so no emit
            // happens. EOF eventually fires when wall-clock now exceeds
            // TotalDuration AND _nextFrameIndex has walked off the end.
            // See Decision 6: range excluding all frames → PlaybackEnded
            // still fires with Error=null on EOF.
            while (_nextFrameIndex < _frames.Count
                && _frames[_nextFrameIndex].Timestamp < startOrMin)
            {
                _nextFrameIndex++;
            }
            while (_nextFrameIndex < _frames.Count
                && _frames[_nextFrameIndex].Timestamp <= now
                && _frames[_nextFrameIndex].Timestamp >= startOrMin
                && _frames[_nextFrameIndex].Timestamp <= endOrMax)
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
                catch (Exception ex)
                {
                    // v1.4.2 PATCH Item 3: surface first sink failure to service
                    // (not silent drop). Capture first only; subsequent throws
                    // are logged but not propagated.
                    if (_sinkException is null)
                    {
                        _sinkException = ex;
                        _onSinkThrew?.Invoke(ex);
                        _isPlaying = false;  // stop playback
                    }
                    break;  // exit foreach; no more frames this tick
                }
            }
        }
        // Raise callback OUTSIDE the lock so subscribers can call back into the
        // service without deadlocking. Carry _sinkException (if any) for the
        // PlaybackEndedEventArgs.Error payload (v1.4.2 PATCH Item 3).
        if (endReached) _onPlaybackEnded?.Invoke(new PlaybackEndedEventArgs(_sinkException));
    }
}
