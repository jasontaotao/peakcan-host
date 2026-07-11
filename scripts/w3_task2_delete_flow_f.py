"""Delete Flow F methods from TraceViewerViewModel.cs (Task 2)."""
from pathlib import Path

MAIN = Path(r"D:/claude_proj2/peakcan-host/src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs")

content = MAIN.read_text(encoding="utf-8")

# Delete AttachAllServiceHandlers + DetachAllServiceHandlers + OnMasterPlaybackEnded + OnAnyFrameEmitted
old_block = """    private void AttachAllServiceHandlers()
    {
        foreach (var (sourceId, svc) in _allServices)
        {
            svc.FrameEmitted += OnAnyFrameEmitted;
            // Only the master drives the loop rewind — non-masters use
            // Loop=false so they don't independently wrap (avoids per-timer drift).
            if (sourceId == MasterSourceId)
                svc.PlaybackEnded += OnMasterPlaybackEnded;
        }
    }

    private void DetachAllServiceHandlers()
    {
        foreach (var svc in _allServices.Values)
        {
            svc.FrameEmitted -= OnAnyFrameEmitted;
            // PlaybackEnded was only subscribed on master — detach defensively
            // (idempotent on services that never had the handler).
            svc.PlaybackEnded -= OnMasterPlaybackEnded;
        }
    }

    // v3.3.0 MINOR: single master-driven rewind anchor. When master EOFs
    // with Loop=true, rewind all services proportionally and resume play.
    private void OnMasterPlaybackEnded(object? sender, PlaybackEndedEventArgs e)
    {
        if (!Loop) return;
        if (e.Error is not null) return;   // sink error / abnormal end — don't auto-loop
        SeekAllToProportionalTime(0.0);
        foreach (var svc in _allServices.Values)
            if (svc.State != ReplayState.Playing)
                svc.Play();
    }

    /// <summary>
    /// FrameEmitted is invoked on the timeline's timer thread. We Post
    /// the cursor advance to the captured
    /// <see cref="SynchronizationContext"/> so the binding writes happen
    /// on the UI thread. Test path (no captured context) sets the
    /// cursor directly — safe because tests assert immediately after
    /// raising the event.
    /// </summary>
    private void OnAnyFrameEmitted(ReplayFrame frame)
    {
        // v3.16.3 PATCH BUGFIX: also update ScrubberValue so the UI
        // scrubber follows playback. The v3.3.0 architecture was
        // scrubber-driven (drag → seek), but Playback left the scrubber
        // frozen because FrameEmitted never wrote back to ScrubberValue.
        // WPF data binding throttles the visible slider motion naturally
        // (DataBinding is coalesced per render frame).
        var t = _masterService?.CurrentTimestamp ?? 0.0;
        if (_syncContext is not null)
            _syncContext.Post(_ =>
            {
                ChartViewModel.UpdatePlaybackCursor(t);
                ScrubberValue = t;
            }, null);
        else
        {
            ChartViewModel.UpdatePlaybackCursor(t);
            ScrubberValue = t;
        }
    }
    private int _onAnyFrameEmittedCount;

"""
assert old_block in content, "Flow F block not found"
content = content.replace(old_block, "")
print("Flow F methods deleted")

MAIN.write_text(content, encoding="utf-8")
print(f"New file size: {MAIN.stat().st_size} bytes")