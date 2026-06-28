namespace PeakCan.Host.Core.Replay;

/// <summary>
/// Replays a recorded ASC trace file. Stateful, thread-safe.
/// Public state via properties; transitions via methods.
/// </summary>
public interface IReplayService
{
    /// <summary>Current playback state.</summary>
    ReplayState State { get; }

    /// <summary>Current playback position (seconds from recording start).</summary>
    double CurrentTimestamp { get; }

    /// <summary>Total duration of the loaded file (seconds from first to last frame).</summary>
    double TotalDuration { get; }

    /// <summary>Current playback speed multiplier (1.0 = realtime).</summary>
    double Speed { get; }

    /// <summary>Fired when a frame is emitted during playback.</summary>
    event Action<ReplayFrame>? FrameEmitted;

    /// <summary>Load and parse an ASC file from disk.</summary>
    /// <exception cref="ReplayLoadException">File not found or IO error.</exception>
    /// <exception cref="ReplayFormatException">File contents cannot be parsed.</exception>
    Task LoadAsync(string path, CancellationToken ct = default);

    /// <summary>Start or resume playback at <see cref="CurrentTimestamp"/>.</summary>
    void Play();

    /// <summary>Halt playback; <see cref="CurrentTimestamp"/> preserved.</summary>
    void Pause();

    /// <summary>Resume from paused state.</summary>
    void Resume();

    /// <summary>Jump to the specified timestamp; resumes position but not state.</summary>
    void Seek(double timestamp);

    /// <summary>Change playback speed multiplier. Must be > 0.</summary>
    void SetSpeed(double multiplier);

    /// <summary>Stop playback and reset to t=0.</summary>
    void Stop();
}