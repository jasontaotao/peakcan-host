using Microsoft.Extensions.Logging;
using PeakCan.Host.Core.Path;

namespace PeakCan.Host.Core.Replay;

/// <summary>
/// DI-singleton implementation of <see cref="IReplayService"/>. Owns a
/// <see cref="ReplayTimeline"/> + a reference to the DI-injected
/// <see cref="IReplayFrameSink"/> for frame emission.
/// </summary>
public sealed partial class ReplayService : IReplayService, IDisposable
{
    private readonly IReplayFrameSink _sink;
    private readonly ILogger<ReplayService> _logger;
    private readonly ReplayTimeline _timeline;
    private IReadOnlyList<ReplayFrame> _frames = Array.Empty<ReplayFrame>();
    // v1.4.2 PATCH Item 3: captured first sink exception. Set once; surfaced
    // via PlaybackEndedEventArgs.Error. Set-only-once so first-failure wins.
    private Exception? _sinkException;
    internal Exception? SinkExceptionForTesting => _sinkException;

    public ReplayService(IReplayFrameSink sink, ILogger<ReplayService> logger)
    {
        _sink = sink;
        _logger = logger;
        // The timeline raises the playback-ended callback on its timer thread;
        // we forward to the public PlaybackEnded event from there. The
        // onSinkThrew callback carries sink exceptions out of the timeline
        // (which would otherwise be swallowed by OnTick's catch block) so
        // we can populate PlaybackEndedEventArgs.Error. v1.4.2 PATCH Item 3.
        _timeline = new ReplayTimeline(
            EmitFrame,
            onPlaybackEnded: RaisePlaybackEnded,
            onSinkThrew: OnSinkThrewFromTimeline,
            // v3.9.0 MINOR P1: pass the active-loop-region getter so the
            // timeline can read the current region on each OnTick. The
            // getter closes over _activeLoopRegion (set via the
            // ActiveLoopRegion property below) so the VM can change the
            // active region mid-playback without rebuilding the timeline.
            activeLoopRegion: () => _activeLoopRegion,
            onLoopRewound: r => LoopRewound?.Invoke(this,
                new LoopRegionRewoundEventArgs(r.Start, r.End)),
            // v3.14.0 MINOR A7: pass our logger so the
            // LogInvalidLoopRegion warning lands in the same Serilog
            // pipeline as the rest of the Replay subsystem's diagnostics.
            logger: _logger);
    }

    public ReplayState State => !_timeline.HasStarted
        ? ReplayState.Stopped
        : _timeline.IsPlaying ? ReplayState.Playing : ReplayState.Paused;
    public double CurrentTimestamp => _timeline.CurrentTimestamp;
    public double TotalDuration => _frames.Count > 0 ? _frames[^1].Timestamp : 0.0;

    /// <summary>
    /// v3.8.0 MINOR chunk 1: live read-only view of the parsed frames.
    /// Returns the internal list (callers must not mutate). Empty before
    /// <see cref="LoadAsync"/> succeeds; replaced atomically on each reload.
    /// </summary>
    public IReadOnlyList<ReplayFrame> Frames => _frames;

    public double Speed => _timeline.Speed;
    public event Action<ReplayFrame>? FrameEmitted;

    public bool Loop
    {
        get => _timeline.Loop;
        set => _timeline.Loop = value;
    }

    public IReadOnlySet<uint>? CanIdFilter { get; set; }

    /// <summary>
    /// v1.5.1 PATCH Task 2: proxies to <see cref="ReplayTimeline.StartTimestamp"/>.
    /// See <see cref="IReplayService.StartTimestamp"/> for the full contract.
    /// </summary>
    public double? StartTimestamp
    {
        get => _timeline.StartTimestamp;
        set => _timeline.StartTimestamp = value;
    }

    /// <summary>
    /// v1.5.1 PATCH Task 2: proxies to <see cref="ReplayTimeline.EndTimestamp"/>.
    /// See <see cref="IReplayService.EndTimestamp"/> for the full contract.
    /// </summary>
    public double? EndTimestamp
    {
        get => _timeline.EndTimestamp;
        set => _timeline.EndTimestamp = value;
    }

    // v3.9.0 MINOR P1: backing field for ActiveLoopRegion. The
    // timeline reads it via a Func getter passed in the ctor — the
    // getter closes over this field, so the timeline observes the
    // current value on each OnTick.
    private (double Start, double End)? _activeLoopRegion;

    /// <summary>
    /// v3.9.0 MINOR P1: see <see cref="IReplayService.ActiveLoopRegion"/>
    /// for the full contract. Setting this property updates the
    /// backing field that the timeline's OnTick reads via the ctor's
    /// <c>activeLoopRegion</c> Func getter — no timeline
    /// reconstruction required.
    /// </summary>
    public (double Start, double End)? ActiveLoopRegion
    {
        get => _activeLoopRegion;
        set => _activeLoopRegion = value;
    }

    /// <summary>
    /// v3.9.0 MINOR P1: re-raises the timeline's loop-rewound
    /// callback as the public <see cref="IReplayService.LoopRewound"/>
    /// event. UI subscribers (typically <c>ReplayViewModel</c>) attach
    /// to this event to surface status messages.
    /// </summary>
    public event EventHandler<LoopRegionRewoundEventArgs>? LoopRewound;

    public event EventHandler<PlaybackEndedEventArgs>? PlaybackEnded;



    /// <summary>
    /// Disposes the playback timer. Safe to call multiple times.
    /// </summary>
    public void Dispose() => _timeline.Stop();


    public void Play() => _timeline.Play();
    public void Pause() => _timeline.Pause();
    public void Resume() => _timeline.Play();
    public void Seek(double timestamp) => _timeline.Seek(timestamp);
    public void SetSpeed(double multiplier) => _timeline.SetSpeed(multiplier);
    public void Stop() => _timeline.Stop();



    // v3.14.0 MINOR A6: EmitFrameToSinkAsync now runs inside a
    // Task.Run (fire-and-forget) from EmitFrame on the timer thread,
    // so the await is observed by the threadpool, not the timer.
    // analyzer flags for using LoggerExtensions instead of a generated delegate.
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Replay sink threw for frame {FrameId} at t={Timestamp}")]
    private static partial void LogSinkThrew(ILogger logger, Exception ex, uint frameId, double timestamp);
}
