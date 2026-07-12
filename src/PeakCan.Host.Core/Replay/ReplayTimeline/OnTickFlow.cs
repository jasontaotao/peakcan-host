using System.Collections.Generic;

namespace PeakCan.Host.Core.Replay;

internal sealed partial class ReplayTimeline
{
    // Flow B: OnTick (v1.4.2 PATCH Item 3 + v1.5.1 PATCH + v3.9.0 MINOR P1 + v3.14.0 MINOR A7 + v3.16.7 DIAG + earlier).
    // Single largest method (179 LoC) extracted to its own partial per
    // W14 D8 sister-principle (one-method-one-partial when method > ~175 LoC).
    // Stays inline per W12 D7 + W14 D8 (helper-extraction would require
    // changing the method shape — current body is one continuous
    // lock-region-emit-region-call-back block).

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
            // - the cursor's wall-clock "now" naturally catches up to
            // the first in-range frame's Timestamp over time.
            //
            // Above-bound case (cursor past endOrMax, e.g. Seek past End):
            // not pre-skipped here - the main while-loop predicate filters
            // such frames out (Timestamp <= endOrMax fails), so no emit
            // happens. EOF eventually fires when wall-clock now exceeds
            // TotalDuration AND _nextFrameIndex has walked off the end.
            // See Decision 6: range excluding all frames -> PlaybackEnded
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
            // Loop=true (which rewinds to t=0 on EOF) - if both are active,
            // region rewind fires FIRST on each region.End crossing; the
            // file-level Loop rewind only fires if region.Start == 0.
            //
            // Why condition on >= (not >): the last emitted frame's
            // Timestamp is exactly the cursor's position. A region [2,4]
            // and a frame at t=4 means "4 is in the region" - rewind at
            // t=4 is the correct UX. Strict > would delay the rewind by
            // 1 tick (~1 ms at 1x), creating a visible "pause at t=4"
            // before the rewind.
            //
            // Why this check is INSIDE the lock: the rewind mutates
            // _currentTimestamp, _playStartTimestamp, _playStartWallClock,
            // and _nextFrameIndex - all under the same lock as the
            // emit-loop mutations. Atomicity guarantees the next tick
            // sees the rewound state.
            if (_activeLoopRegionGetter is { } getRegion)
            {
                var region = getRegion();
                if (region is { } r)
                {
                    // v3.14.0 MINOR A7: defensive guard against user-supplied
                    // Start > End (ReplayViewModel.ActiveLoopRegion setter
                    // doesn't validate - see IReplayService.StartTimestamp
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
            // Loop=true -> rewind to t=0 and continue. Loop=false -> stop and raise
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
}
