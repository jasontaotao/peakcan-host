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
    // === Flow A methods moved to ReplayTimeline/PlaybackLifecycleFlow.cs (W15 Task 1) ===
    // === Flow B methods moved to ReplayTimeline/OnTickFlow.cs (W15 Task 2) ===
}
