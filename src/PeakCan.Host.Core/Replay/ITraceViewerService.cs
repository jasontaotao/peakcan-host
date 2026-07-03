namespace PeakCan.Host.Core.Replay;

/// <summary>
/// Trace Viewer service: load an ASC recording and play it back for
/// INSPECTION ONLY. Sibling of <see cref="IReplayService"/>, but with
/// NO <see cref="IReplayFrameSink"/> involvement — frames are never
/// written to the CAN bus. Used by the Trace Viewer window to let
/// engineers review what happened in a recorded session.
/// <para>
/// All public methods and event raises are thread-safe; the VM layer
/// is responsible for marshaling to the UI thread.
/// </para>
/// </summary>
public interface ITraceViewerService
{
    ReplayState State { get; }
    double CurrentTimestamp { get; }
    double TotalDuration { get; }
    double Speed { get; }

    /// <summary>Fired on the timeline's timer thread. UI subscribers must marshal.</summary>
    event Action<ReplayFrame>? FrameEmitted;

    bool Loop { get; set; }
    IReadOnlySet<uint>? CanIdFilter { get; set; }
    double? StartTimestamp { get; set; }
    double? EndTimestamp { get; set; }

    /// <summary>Fired on the timeline's timer thread. UI subscribers must marshal.</summary>
    event EventHandler<PlaybackEndedEventArgs>? PlaybackEnded;

    Task LoadAsync(string path, CancellationToken ct = default);
    void Play();
    void Pause();
    void Resume();
    void Seek(double timestamp);
    void SetSpeed(double multiplier);
    void Stop();
}
