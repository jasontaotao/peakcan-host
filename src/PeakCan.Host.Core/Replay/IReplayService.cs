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

    /// <summary>
    /// Read-only view of the parsed frames, in source order. Empty before <see cref="LoadAsync"/>.
    /// <para>
    /// Live reference to the internal list — callers MUST NOT mutate. Used by
    /// <c>ReplayViewModel</c> for frame-step binary search and bookmark/loop-region
    /// timestamp resolution. Frames are immutable <see cref="ReplayFrame"/> records.
    /// </para>
    /// </summary>
    IReadOnlyList<ReplayFrame> Frames { get; }

    /// <summary>Current playback speed multiplier (1.0 = realtime).</summary>
    double Speed { get; }

    /// <summary>Fired when a frame is emitted during playback.</summary>
    /// <remarks>
    /// Fired on the timer callback thread, NOT the calling thread. UI subscribers
    /// must marshal to the UI thread (e.g., Dispatcher.InvokeAsync).
    /// </remarks>
    event Action<ReplayFrame>? FrameEmitted;

    /// <summary>
    /// If true, playback restarts from t=0 upon reaching <see cref="TotalDuration"/>.
    /// If false (default), playback auto-stops and <see cref="PlaybackEnded"/> is raised.
    /// </summary>
    bool Loop { get; set; }

    /// <summary>
    /// Tri-state CAN-ID filter applied at the emit boundary:
    /// <list type="bullet">
    ///   <item><description><c>null</c> (default) — all frames pass through.</description></item>
    ///   <item><description>Empty set — no frames pass through (distinct from null).</description></item>
    ///   <item><description>Non-empty set — only frames whose <see cref="ReplayFrame.Id"/> is in the set pass through.</description></item>
    /// </list>
    /// Filter changes take effect on the next emit (no buffering of stale decisions).
    /// </summary>
    IReadOnlySet<uint>? CanIdFilter { get; set; }

    /// <summary>
    /// Inclusive lower bound on emitted frames' <see cref="ReplayFrame.Timestamp"/>
    /// (seconds from recording start). <c>null</c> (default) means unbounded below.
    /// <para>
    /// The range filter is enforced at the timeline iteration boundary
    /// (NOT the emit boundary), so the cursor skips frames outside the
    /// window — <see cref="CurrentTimestamp"/> advances only across
    /// in-range frames. Composes with <see cref="CanIdFilter"/>: range
    /// filter is applied first at the iteration boundary, CAN-ID filter
    /// second at the emit boundary. Re-applied after <see cref="Loop"/>
    /// rewind — a loop rewind to t=0 walks the cursor forward through
    /// the start bound before emitting again.
    /// </para>
    /// <para>
    /// Changes take effect on the next emit (no buffering of stale
    /// decisions). The service does NOT validate <c>Start &lt;= End</c> —
    /// the VM is responsible for that to keep the WPF two-way binding
    /// path free of exceptions.
    /// </para>
    /// </summary>
    double? StartTimestamp { get; set; }

    /// <summary>
    /// Inclusive upper bound on emitted frames' <see cref="ReplayFrame.Timestamp"/>
    /// (seconds from recording start). <c>null</c> (default) means unbounded above.
    /// Same composition + re-application semantics as <see cref="StartTimestamp"/>.
    /// </summary>
    double? EndTimestamp { get; set; }

    /// <summary>
    /// Raised once when playback reaches <see cref="TotalDuration"/> with
    /// <see cref="Loop"/> == false, OR when playback is aborted due to a
    /// sink failure (e.g. <see cref="ReplaySendException"/>). UI listens
    /// to reset the scrubber, show "Playback ended" hint, or surface the
    /// error via <see cref="PlaybackEndedEventArgs.Error"/>. Not raised
    /// when <see cref="Loop"/> is true.
    /// </summary>
    /// <remarks>
    /// Fired on the timer callback thread, NOT the calling thread. UI subscribers
    /// must marshal to the UI thread (e.g., Dispatcher.InvokeAsync).
    /// </remarks>
    event EventHandler<PlaybackEndedEventArgs>? PlaybackEnded;

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

    /// <summary>Jump to the specified timestamp. Does not change the playing/paused state.</summary>
    void Seek(double timestamp);

    /// <summary>Change playback speed multiplier. Must be > 0.</summary>
    void SetSpeed(double multiplier);

    /// <summary>Stop playback and reset to t=0.</summary>
    void Stop();
}
