namespace PeakCan.Host.Core.Replay;

/// <summary>
/// Streaming playback engine: pulls frames from an <see cref="IStreamingTraceSource"/>,
/// pacing emission by each frame's timestamp × <see cref="Speed"/>. Read-only
/// (no bus writes). <see cref="SeekAsync"/> re-opens the source and fast-forwards
/// to the target timestamp without emitting skipped frames.
/// </summary>
public interface IStreamingTracePlayer : IDisposable
{
    ReplayState State { get; }
    double CurrentTimestamp { get; }
    double Speed { get; }
    long FramesEmitted { get; }

    /// <summary>Skipped malformed lines reported by the current open session.</summary>
    long SkippedLines { get; }

    event Action<ReplayFrame>? FrameEmitted;
    event EventHandler<PlaybackEndedEventArgs>? PlaybackEnded;
    event Action<double>? SeekProgress;

    /// <summary>Start or resume playback. Completes at EOF, failure, external cancellation, or user stop. <see cref="PlaybackEnded"/> fires only for EOF or failure.</summary>
    Task PlayAsync(CancellationToken ct = default);
    void Pause();
    void Resume();
    void SetSpeed(double multiplier);
    /// <summary>Re-open the source and resume from <paramref name="timestamp"/>. Returns to the pre-seek play state (Playing→续播, Paused→停在该帧).</summary>
    Task SeekAsync(double timestamp, CancellationToken ct = default);
    void Stop();
}
