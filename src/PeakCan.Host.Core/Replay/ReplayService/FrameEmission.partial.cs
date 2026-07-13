// ReplayService/FrameEmission.partial.cs — W31 T2 (Flow B, 69 LoC)
// Frame-emission helpers: EmitFrame (filter check + Task.Run fire-and-forget
// sink dispatch + FrameEmitted event raise) + EmitFrameToSinkAsync (await
// _sink.SendFrameAsync) + OnSinkThrewFromTimeline (capture-first-exception +
// timeline.Pause + RaisePlaybackEnded) + RaisePlaybackEnded (forward
// PlaybackEnded event).
//
// Cross-partial caller pattern: ctor (in main) passes EmitFrame as a
// delegate to ReplayTimeline ctor — partial-class cross-partial visibility
// handles this automatically (sister of W22+W23+W24+W25+W26+W27+W28+W29+W30).
//
// [LoggerMessage] partial declaration LogSinkThrew stays on main per W18+W22
// +W23+W25+W26+W27+W28+W29+W30+W31 sister precedent (CS8795 mitigation).
// Called from EmitFrame (in this partial) — cross-partial call resolution
// handles this automatically.
//
// W23 STRUCT-FABRICATION LESSON: Task.Run(Func<Task>) async signature +
// _sink.SendFrameAsync(ReplayFrame, CancellationToken) signature +
// _timeline.Pause() signature verified during verbatim re-extraction.
//
// W31 T2 verbatim re-extracted via `git show main:src/.../ReplayService.cs | sed -n '122,129p;131,145p;211,249p;251,257p'`
// per W20 T2 R1 fabrication LESSON (38th application).

using Microsoft.Extensions.Logging;

namespace PeakCan.Host.Core.Replay;

public sealed partial class ReplayService
{
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

    private void EmitFrame(ReplayFrame frame)
    {
        // v1.5.0 MINOR Task 4: tri-state CAN-ID filter. null = pass all;
        // empty set = pass none; non-empty = only matching IDs.
        var filter = CanIdFilter;
        if (filter is not null && !filter.Contains(frame.Id))
        {
            return; // filter rejects this frame; no sink call, no event raise
        }

        // v3.14.0 MINOR A6: fire-and-forget. Sync wait on a 1ms timer
        // thread blocks the entire timeline when the PEAK driver blocks
        // (USB unplug / driver busy). The self-contradicting xmldoc
        // previously here claimed "intentionally fire-and-forget" but
        // the implementation was sync wait — implementation now matches
        // the intent. ReplaySendException no longer rethrows from the
        // timer thread; it propagates via OnSinkThrewFromTimeline on the
        // threadpool, which captures it as first-failure + pauses +
        // raises PlaybackEnded (same first-failure-wins contract as
        // v1.4.2 PATCH Item 3, just not on the timer thread anymore).
        // Other exceptions are logged + swallowed (preserves the
        // v1.4.0 tolerance for non-send failures).
        _ = Task.Run(async () =>
        {
            try
            {
                await EmitFrameToSinkAsync(frame).ConfigureAwait(false);
            }
            catch (ReplaySendException ex)
            {
                OnSinkThrewFromTimeline(ex);
            }
            catch (Exception ex)
            {
                LogSinkThrew(_logger, ex, frame.Id, frame.Timestamp);
            }
        });
        FrameEmitted?.Invoke(frame);
    }

    // v3.14.0 MINOR A6: EmitFrameToSinkAsync now runs inside a
    // Task.Run (fire-and-forget) from EmitFrame on the timer thread,
    // so the await is observed by the threadpool, not the timer.
    private async Task EmitFrameToSinkAsync(ReplayFrame frame)
    {
        await _sink.SendFrameAsync(frame, CancellationToken.None).ConfigureAwait(false);
    }
}
