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
    /// v3.9.0 MINOR P1: A/B loop-region auto-rewind. When the playback
    /// cursor reaches or crosses the active loop region's
    /// <c>End</c>, the timeline atomically rewinds to <c>Start</c> and
    /// continues emitting. <c>null</c> (default) = no loop region
    /// (playback runs through to EOF; <see cref="PlaybackEnded"/> fires
    /// once unless <see cref="Loop"/> is true).
    /// <para>
    /// The tuple's components are unpacked from
    /// <c>LoopRegionDto.Start</c> / <c>LoopRegionDto.End</c> by the VM
    /// (Core has no dependency on the App-layer DTO). Changing the
    /// active region mid-playback takes effect on the next
    /// <c>OnTick</c> — the timeline reads the property via a Func
    /// getter so the change is observed atomically.
    /// </para>
    /// <para>
    /// The region can be set BEFORE <see cref="LoadAsync"/> — the
    /// service holds the property but the timeline doesn't read it
    /// until playback starts. Setting it to a non-null value while
    /// paused re-evaluates the rewind condition on the next Play
    /// tick.
    /// </para>
    /// </summary>
    (double Start, double End)? ActiveLoopRegion { get; set; }

    /// <summary>
    /// v3.9.0 MINOR P1: raised when playback is rewound to the active
    /// loop region's <c>Start</c> after crossing <c>End</c>. UI
    /// subscribers (typically <c>ReplayViewModel</c>) use this to
    /// surface a status message ("Rewind: loop region X") and reset
    /// the visual scroll position.
    /// <para>
    /// Fired on the timer callback thread, NOT the calling thread. UI
    /// subscribers must marshal to the UI thread (e.g.,
    /// <c>Dispatcher.InvokeAsync</c>). Same threading contract as
    /// <see cref="FrameEmitted"/> and <see cref="PlaybackEnded"/>.
    /// </para>
    /// </summary>
    event EventHandler<LoopRegionRewoundEventArgs>? LoopRewound;

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

    /// <summary>
    /// v3.8.4 PATCH H2: drop the loaded frame buffer and reset internal
    /// timeline state without starting or stopping playback. Used by
    /// <c>ReplayViewModel.OpenSessionAsync</c> when a multi-source bundle
    /// fails to load (any source missing) — the VM-side bindable surface
    /// is cleared in the missing branch, but the service-side
    /// <see cref="Frames"/> list still holds whatever source succeeded
    /// last. Calling <c>Reset()</c> here keeps the service's authoritative
    /// state in sync with the VM's reported <c>IsLoaded = false</c>.
    /// <para>
    /// Distinct from <see cref="Stop"/>: <c>Stop</c> halts the timer and
    /// resets the cursor to t=0 but does NOT clear <c>_frames</c>.
    /// <c>Reset</c> clears <c>_frames</c> in addition to stopping the
    /// timeline, leaving the service in a "no file loaded" state.
    /// </para>
    /// </summary>
    void Reset();
}
