namespace PeakCan.HIL.Core.Replay;

internal sealed partial class ReplayTimeline
{
    // Flow A: PlaybackLifecycle (v1.5.1 PATCH + v3.9.0 MINOR + v3.14.0 MINOR A7 + v3.16.9.3 PATCH + earlier).
    // Play + Pause + Seek + SetSpeed + Stop + PlayedTimestamp kept together as
    // ONE partial per W14 D2 + W3 R3 sister lesson (mutable-state coupling on
    // _isPlaying + _playStartWallClock + _playStartTimestamp + _currentTimestamp).
    //
    // Cross-flow callers (partial-class visible):
    //   - Play's Timer callback OnTick (Flow B)
    //   - OnTick reads PlayedTimestamp (Flow B)

    public void Play()
    {
        LogPlayEntry(_logger, _frames.Count, _isPlaying, _hasStarted, _currentTimestamp);
        lock (_lock)
        {
            if (_isPlaying)
            {
                LogPlayAlreadyRunning(_logger);
                return;
            }
            if (_frames.Count == 0)
            {
                LogPlayNoFrames(_logger);
                return; // nothing to play; leave state as Stopped
            }
            _playStartWallClock = _clock.Now;
            _playStartTimestamp = _currentTimestamp;
            _isPlaying = true;
            _hasStarted = true;
            // v3.16.7 DIAG: 1ms tick. Kept (not raised to 16ms) because
            // FakeReplayClock.TickRepeated(N) advances _now by the timer
            // period per tick — raising the period to 16ms would re-interpret
            // every existing TickRepeated(N) call as 16N ms instead of N ms
            // and break ~30 timeline tests that were written against the 1ms
            // semantics. The real CPU sink was the v3.16.7 per-tick
            // Information logs (demoted to Debug in v3.18.2), not the tick
            // rate itself (each tick is O(1): one lock + one while-walk).
            _timer ??= _clock.CreateTimer(OnTick, null, dueTime: TimeSpan.FromMilliseconds(1), period: TimeSpan.FromMilliseconds(1));
            LogPlayStarted(_logger, _currentTimestamp, _frames.Count);
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (!_isPlaying) return;
            _isPlaying = false;
        }
    }

    public void Seek(double timestamp)
    {
        lock (_lock)
        {
            _currentTimestamp = timestamp;
            _playStartTimestamp = timestamp;
            _playStartWallClock = _clock.Now;
            // Advance next-frame index to first frame with Timestamp >= target
            _nextFrameIndex = 0;
            while (_nextFrameIndex < _frames.Count && _frames[_nextFrameIndex].Timestamp < timestamp)
            {
                _nextFrameIndex++;
            }
        }
    }

    public void SetSpeed(double multiplier)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(multiplier);
        lock (_lock)
        {
            // v3.16.9.3 PATCH: re-anchor wallclock BEFORE computing
            // PlayedTimestamp. The previous order computed PlayedTimestamp
            // first (using the stale _playStartWallClock, which is
            // DateTime.MinValue on a never-played timeline), then
            // updated wallclock. With MinValue wallclock, PlayedTimestamp
            // = _playStartTimestamp + (UtcNow - MinValue) * _speed is a
            // ~6×10^10 second offset - a value well beyond any real
            // trace timestamp. This leaked into _currentTimestamp and
            // propagated to master.CurrentTimestamp, which the VM
            // (via OnAnyFrameEmitted or other paths) then wrote to
            // ScrubberValue, which (without v3.16.9.2 guard checking
            // master.State) triggered SeekAllToProportionalTime with
            // an absurd value, snapping the scrubber to the trace
            // end. User symptom: "progress bar jumps straight to
            // end" on the very first frame after AddTraceAsync.
            //
            // Fix: update wallclock FIRST so the subsequent
            // PlayedTimestamp calculation uses the new wallclock (and
            // elapsed is 0 for a never-played timeline -> PlayedTimestamp
            // = _playStartTimestamp = 0).
            _playStartWallClock = _clock.Now;
            _currentTimestamp = PlayedTimestamp;
            _playStartTimestamp = _currentTimestamp;
            _speed = multiplier;
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _isPlaying = false;
            _hasStarted = false;
            _currentTimestamp = 0.0;
            _nextFrameIndex = 0;
            _playStartTimestamp = 0.0;
        }
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>
    /// Wall-clock-adjusted timestamp of the current playback position.
    /// = play_start_timestamp + (now - play_start_wall_clock) * speed.
    /// </summary>
    private double PlayedTimestamp
    {
        get
        {
            var elapsed = (_clock.Now - _playStartWallClock).TotalSeconds * _speed;
            return _playStartTimestamp + elapsed;
        }
    }
}
