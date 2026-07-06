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
            onSinkThrew: OnSinkThrewFromTimeline);
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

    public event EventHandler<PlaybackEndedEventArgs>? PlaybackEnded;

    /// <summary>
    /// Forwards the timeline's playback-ended callback to the public event.
    /// Invoked on the timer thread; UI subscribers must marshal to the UI thread.
    /// v1.4.2 PATCH Item 3: carries <see cref="PlaybackEndedEventArgs.Error"/>
    /// from the timeline so UI can surface sink failures.
    /// </summary>
    private void RaisePlaybackEnded(PlaybackEndedEventArgs args)
        => PlaybackEnded?.Invoke(this, args);

    /// <summary>
    /// v1.4.2 PATCH Item 3: invoked by the timeline when a sink callback
    /// throws (e.g. <see cref="ReplaySendException"/> from a failed
    /// <c>SendService.SendAsync</c>). Captures the first exception, pauses
    /// the timeline, and raises <see cref="PlaybackEnded"/> with the error.
    /// </summary>
    private void OnSinkThrewFromTimeline(Exception ex)
    {
        if (_sinkException is null)
        {
            _sinkException = ex;
            _timeline.Pause();  // ensure _isPlaying=false
            RaisePlaybackEnded(new PlaybackEndedEventArgs(ex));
        }
    }

    /// <summary>
    /// Disposes the playback timer. Safe to call multiple times.
    /// </summary>
    public void Dispose() => _timeline.Stop();

    public async Task LoadAsync(string path, CancellationToken ct = default)
    {
        // v3.8.5 PATCH H1: defensive reset on entry. Clear `_frames` +
        // push to the timeline BEFORE the parse attempt so a failed
        // load (parse exception, file not found, IO error) leaves the
        // service in a clean "no file loaded" state rather than
        // silently retaining the prior file's frames. Defense-in-depth
        // alongside the v3.8.4 H2 `Reset()` call from OpenSessionAsync
        // -- this LoadAsync-level reset fires automatically without
        // caller cooperation.
        _frames = Array.Empty<ReplayFrame>();
        _timeline.SetFrames(_frames);

        // v1.4.0 MINOR Replay: open file → parse → set frames.
        // ParseExceptions propagate; FileNotFound/IO wrap into ReplayLoadException.
        try
        {
            await using var fs = File.OpenRead(PathNormalizer.Normalize(path));
            _frames = await AscParser.ParseAsync(fs, ct).ConfigureAwait(false);
        }
        catch (ReplayException) { throw; }
        catch (FileNotFoundException ex)
        {
            throw new ReplayLoadException($"ASC file not found: {path}", ex);
        }
        catch (Exception ex)
        {
            throw new ReplayLoadException($"Failed to read ASC file: {path}", ex);
        }
        _timeline.SetFrames(_frames);
    }

    public void Play() => _timeline.Play();
    public void Pause() => _timeline.Pause();
    public void Resume() => _timeline.Play();
    public void Seek(double timestamp) => _timeline.Seek(timestamp);
    public void SetSpeed(double multiplier) => _timeline.SetSpeed(multiplier);
    public void Stop() => _timeline.Stop();

    /// <summary>
    /// v3.8.4 PATCH H2: drop the loaded frame buffer and reset the
    /// internal timeline. After <c>Reset</c>, <see cref="Frames"/> is
    /// empty and <see cref="TotalDuration"/> is 0.0; the service is in
    /// the same "no file loaded" state as a freshly-constructed instance
    /// (the timer is stopped by <c>_timeline.Stop()</c>).
    /// <para>
    /// Used by <c>ReplayViewModel.OpenSessionAsync</c> on the
    /// failure-teardown branch. Distinct from <see cref="Stop"/>, which
    /// only halts the timer (frames are preserved so a subsequent
    /// <c>Play()</c> can resume).
    /// </para>
    /// </summary>
    public void Reset()
    {
        _timeline.Stop();
        _frames = Array.Empty<ReplayFrame>();
        _timeline.SetFrames(_frames);
    }

    private void EmitFrame(ReplayFrame frame)
    {
        // v1.5.0 MINOR Task 4: tri-state CAN-ID filter. null = pass all;
        // empty set = pass none; non-empty = only matching IDs.
        var filter = CanIdFilter;
        if (filter is not null && !filter.Contains(frame.Id))
        {
            return; // filter rejects this frame; no sink call, no event raise
        }

        // v1.4.2 PATCH Item 3: block on the sink so a first-failure
        // (ReplaySendException) propagates to the timeline's foreach catch.
        // CAN bus writes are bounded (<1 ms typical), so blocking the 1 ms
        // timer thread is acceptable. ReplaySendException is rethrown to
        // surface via onSinkThrew; other exceptions are logged and swallowed
        // (preserves the v1.4.0 tolerance for non-send failures).
        try
        {
            EmitFrameToSinkAsync(frame).GetAwaiter().GetResult();
        }
        catch (ReplaySendException)
        {
            throw;  // propagate to OnTick foreach catch → OnSinkThrewFromTimeline
        }
        catch (Exception ex)
        {
            LogSinkThrew(_logger, ex, frame.Id, frame.Timestamp);
        }
        FrameEmitted?.Invoke(frame);
    }

    // CA2012: ValueTask is intentionally fire-and-forget — Timer callbacks are
    // synchronous and we cannot await here. The task is stored in a field long
    // enough for the runtime to observe completion; downstream sink errors are
    // caught by the timer-level try/catch in ReplayTimeline.OnTick.
#pragma warning disable CA2012
    private async Task EmitFrameToSinkAsync(ReplayFrame frame)
    {
        await _sink.SendFrameAsync(frame, CancellationToken.None).ConfigureAwait(false);
    }
#pragma warning restore CA2012

    // LoggerMessage source-generated helper (CA1848). Replaces
    // _logger.LogWarning(ex, "...", frame.Id, frame.Timestamp) which the
    // analyzer flags for using LoggerExtensions instead of a generated delegate.
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Replay sink threw for frame {FrameId} at t={Timestamp}")]
    private static partial void LogSinkThrew(ILogger logger, Exception ex, uint frameId, double timestamp);
}
