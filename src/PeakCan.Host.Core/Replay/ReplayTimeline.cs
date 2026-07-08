using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PeakCan.Host.Core.Replay;

/// <summary>
/// Timer-driven playback scheduler. Internal — used only by
/// <see cref="ReplayService"/>. Owns the playback timer; mutates
/// state on <see cref="Play"/>/<see cref="Pause"/>/<see cref="Resume"/>/
/// <see cref="Seek"/>/<see cref="SetSpeed"/>/<see cref="Stop"/>.
/// </summary>
internal sealed partial class ReplayTimeline
{
    private readonly object _lock = new();
    // v3.14.0 MINOR A7: optional logger. Defaults to NullLogger so the
    // existing 25+ test sites that construct ReplayTimeline without an
    // ILogger continue to work. ReplayService passes its ILogger<ReplayService>
    // so the A7 invalid-loop-region warning lands in the same Serilog pipeline
    // as the rest of the Replay subsystem's diagnostics.
    private readonly ILogger _logger;
    private readonly Action<ReplayFrame> _emit;
    private readonly Action<PlaybackEndedEventArgs>? _onPlaybackEnded;
    private readonly Action<Exception>? _onSinkThrew;
    // v3.9.0 MINOR P1: A/B loop rewind callback. Raised outside the
    // lock (in OnTick) when the timeline rewinds the cursor to an
    // active loop region's Start. ReplayService subscribes to this
    // and re-raises it as the public LoopRewound event.
    private readonly Action<(double Start, double End)>? _onLoopRewound;
    // v3.9.0 MINOR P1: A/B loop-region getter. Returns (Start, End) of
    // the currently-active loop region, or null if no region is active.
    // Read on each OnTick so the VM can swap regions mid-playback
    // without reconstructing the timeline. Caller must keep the
    // getter thread-safe (the timeline reads it from the timer thread
    // under its own internal lock; the VM writes from the UI thread).
    private readonly Func<(double Start, double End)?>? _activeLoopRegionGetter;
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
        Action<Exception>? onSinkThrew = null,
        // v3.9.0 MINOR P1: A/B loop-region getter. Optional (defaults
        // to null = no loop region). Returns the active region's
        // (Start, End) bounds, or null if no region is active.
        Func<(double Start, double End)?>? activeLoopRegion = null,
        // v3.9.0 MINOR P1: A/B loop-rewind callback. Raised when the
        // cursor is rewound to region.Start after crossing region.End.
        // Optional (defaults to null = silent rewind).
        Action<(double Start, double End)>? onLoopRewound = null,
        // v3.14.0 MINOR A7: optional logger for diagnostics. Defaults
        // to NullLogger so existing test sites that don't pass one
        // continue to work without change.
        ILogger? logger = null)
    {
        _emit = emit;
        _onPlaybackEnded = onPlaybackEnded;
        _onSinkThrew = onSinkThrew;
        _activeLoopRegionGetter = activeLoopRegion;
        _onLoopRewound = onLoopRewound;
        _logger = logger ?? NullLogger.Instance;
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
        LogPlayEntry(_logger, _frames.Count, _isPlaying, _hasStarted, _currentTimestamp);
        lock (_lock)
        {
            if (_isPlaying)
            {
                LogPlayAlreadyRunning(_logger);
                return;
            }
            if (_frames.Count == 0)
            {
                LogPlayNoFrames(_logger);
                return; // nothing to play; leave state as Stopped
            }
            _playStartWallClock = DateTime.UtcNow;
            _playStartTimestamp = _currentTimestamp;
            _isPlaying = true;
            _hasStarted = true;
            _timer ??= new Timer(OnTick, null, dueTime: 1, period: 1);
            LogPlayStarted(_logger, _currentTimestamp, _frames.Count);
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
        // v3.16.7 DIAG: log every tick to see if timer is actually firing
        LogOnTickEntry(_logger, _isPlaying, _frames.Count, _nextFrameIndex, _currentTimestamp);
        List<ReplayFrame>? toEmit = null;
        bool endReached = false;
        // v3.9.0 MINOR P1: A/B loop-region rewind. Captured inside the
        // lock so the getter is observed atomically with the emit loop.
        // Raised OUTSIDE the lock so the event subscriber (typically a
        // UI thread) can call back into the service without deadlocking.
        (double Start, double End)? rewindRegion = null;
        lock (_lock)
        {
            if (!_isPlaying) { LogOnTickNotPlaying(_logger); return; }
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
            // v3.16.7 DIAG: log how many frames we decided to emit this tick
            if (toEmit is { Count: > 0 })
            {
                LogOnTickEmitting(_logger, toEmit.Count, now, _currentTimestamp);
            }
            // v3.9.0 MINOR P1: A/B loop-region rewind. If the cursor has
            // reached or crossed the active region's End (or EOF after
            // emitting past region.End), atomically rewind to region.Start
            // and continue playback. Composes with the existing file-level
            // Loop=true (which rewinds to t=0 on EOF) — if both are active,
            // region rewind fires FIRST on each region.End crossing; the
            // file-level Loop rewind only fires if region.Start == 0.
            //
            // Why condition on >= (not >): the last emitted frame's
            // Timestamp is exactly the cursor's position. A region [2,4]
            // and a frame at t=4 means "4 is in the region" — rewind at
            // t=4 is the correct UX. Strict > would delay the rewind by
            // 1 tick (~1 ms at 1x), creating a visible "pause at t=4"
            // before the rewind.
            //
            // Why this check is INSIDE the lock: the rewind mutates
            // _currentTimestamp, _playStartTimestamp, _playStartWallClock,
            // and _nextFrameIndex — all under the same lock as the
            // emit-loop mutations. Atomicity guarantees the next tick
            // sees the rewound state.
            if (_activeLoopRegionGetter is { } getRegion)
            {
                var region = getRegion();
                if (region is { } r)
                {
                    // v3.14.0 MINOR A7: defensive guard against user-supplied
                    // Start > End (ReplayViewModel.ActiveLoopRegion setter
                    // doesn't validate — see IReplayService.StartTimestamp
                    // xmldoc "The service does NOT validate Start <= End").
                    // Pre-fix, _currentTimestamp >= r.End immediately
                    // re-triggered on the next tick after a rewind to
                    // r.Start (which is > r.End), burning 100% CPU in an
                    // infinite rewind loop. Skip the rewind; the timeline
                    // will play to natural EOF. _currentTimestamp is left
                    // untouched so the comparison on the next tick is
                    // stable (no flicker) and OnLoopRewound is NOT raised
                    // (no UI rewind event for an invalid region).
                    if (r.Start > r.End)
                    {
                        LogInvalidLoopRegion(_logger, r.Start, r.End);
                    }
                    else if (_currentTimestamp >= r.End)
                    {
                        _currentTimestamp = r.Start;
                        _playStartTimestamp = r.Start;
                        _playStartWallClock = DateTime.UtcNow;
                        // Reset _nextFrameIndex to 0 then walk forward past
                        // every pre-region frame. Why reset to 0: the cursor
                        // may have been AT or PAST region.End in the previous
                        // tick (e.g. _nextFrameIndex == _frames.Count - 1
                        // when the End frame emitted). Walking forward from
                        // 0 finds the first in-range frame (Timestamp >=
                        // region.Start); the existing frame[].Timestamp >
                        // r.Start check filters out the others.
                        // Without the reset, the walk-forward-past loop
                        // would short-circuit (frame[_nextFrameIndex].ts is
                        // > region.Start) and the next tick would have
                        // _nextFrameIndex pointing at a frame past the
                        // region, so the cursor would never re-emit.
                        _nextFrameIndex = 0;
                        while (_nextFrameIndex < _frames.Count
                            && _frames[_nextFrameIndex].Timestamp < r.Start)
                        {
                            _nextFrameIndex++;
                        }
                        rewindRegion = r;
                    }
                }
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
        // v3.9.0 MINOR P1: A/B loop rewind event. The ReplayService
        // subscribes to this and re-raises it as the public LoopRewound
        // event. UI listeners (ReplayViewModel) use it to surface a
        // status message ("Rewind: loop region X") + reset visual
        // scroll position. The tuple's components are unpacked into a
        // 2-arg call so the public EventArgs type can carry them.
        if (rewindRegion is { } rr) _onLoopRewound?.Invoke(rr);
    }

    // LoggerMessage source-generated helper (CA1848). v3.14.0 MINOR A7:
    // raised when the active loop region has Start > End (a user-set
    // inverted region). Logs once per OnTick where the condition is
    // true; consumers of the log can dedupe upstream. The guard itself
    // is in OnTick above.
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Invalid loop region (Start > End: {Start} > {End}); rewind disabled, playback continues to natural EOF")]
    private static partial void LogInvalidLoopRegion(ILogger logger, double start, double end);

    // v3.16.7 DIAG: Play() entry log — fires every time user hits Play
    [LoggerMessage(Level = LogLevel.Information,
        Message = "ReplayTimeline.Play() ENTER: _frames.Count={Count} _isPlaying={Playing} _hasStarted={Started} _currentTimestamp={Ts}")]
    private static partial void LogPlayEntry(ILogger logger, int count, bool playing, bool started, double ts);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "ReplayTimeline.Play() skipped: already playing")]
    private static partial void LogPlayAlreadyRunning(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "ReplayTimeline.Play() skipped: 0 frames loaded")]
    private static partial void LogPlayNoFrames(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "ReplayTimeline.Play() STARTED: timer=1ms, _currentTimestamp={Ts}, _frames.Count={Count}")]
    private static partial void LogPlayStarted(ILogger logger, double ts, int count);

    // v3.16.7 DIAG: OnTick entry — fires every 1ms when timer is running
    [LoggerMessage(Level = LogLevel.Information,
        Message = "OnTick: _isPlaying={Playing} _frames.Count={Count} _nextFrameIndex={Idx} _currentTimestamp={Ts}")]
    private static partial void LogOnTickEntry(ILogger logger, bool playing, int count, int idx, double ts);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "OnTick: !_isPlaying — early return, no emit")]
    private static partial void LogOnTickNotPlaying(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "OnTick EMIT: {Count} frames to emit, now={Now} new _currentTimestamp={Ts}")]
    private static partial void LogOnTickEmitting(ILogger logger, int count, double now, double ts);
}
