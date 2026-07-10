using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class TraceViewerViewModel
{
    // Flow B: Transport playback (Play/Pause/Stop/Seek + scrubber/loop/speed).
    // Methods moved verbatim from TraceViewerViewModel.cs.
    //
    // Cross-flow references (all stay as plain calls via partial-class visibility):
    //   - Play/Pause/Stop/SeekTo → _allServices (Flow A dict)
    //   - OnScrubberValueChanged → SeekAllToProportionalTime (Flow F helper)
    //   - OnLoopChanged → PropagateLoopToAllServices (Flow F helper)
    //   - OnSpeedChanged → PropagateSpeedToAllServices (Flow F helper)
    //
    // [RelayCommand] attribute must travel WITH the method — CommunityToolkit.Mvvm
    // source-gen needs to see it on the same declaration to wire the IRelayCommand.

    [RelayCommand]
    public void Play()
    {
        // v3.16.7 DIAG: log PlayCommand entry with full state
        _logger.LogInformation("TraceViewerViewModel.Play() ENTER: _allServices.Count={Count} masterId={Master} HasSources={Has}",
            _allServices.Count, MasterSourceId, HasSources);
        foreach (var (id, svc) in _allServices)
        {
            _logger.LogInformation("  calling svc.Play() for SourceId={Id} TotalDuration={Dur}",
                id, svc.TotalDuration);
            svc.Play();
        }
    }

    [RelayCommand]
    public void Pause()
    {
        foreach (var svc in _allServices.Values)
            svc.Pause();
    }

    [RelayCommand]
    public void Stop()
    {
        foreach (var svc in _allServices.Values)
            svc.Stop();
        ScrubberValue = 0;
    }

    [RelayCommand]
    public void SeekTo(double t)
    {
        // v3.8.6 PATCH H1: clamp is in SeekAllToProportionalTime
        // (defense-in-depth with v3.8.4 L1 Replay-tab pattern). The
        // ScrubberValue setter is left raw so the slider thumb reflects
        // what the user actually typed/programmatic; the timeline-side
        // clamp rejects out-of-range values before they reach the
        // service. Doing the seek here too would double-call every
        // service — single source of truth is the scrubber change handler.
        ScrubberValue = t;
    }

    partial void OnScrubberValueChanged(double value)
    {
        // v3.16.9.2 PATCH: reverse-trigger guard. Playback's
        // OnAnyFrameEmitted writes ScrubberValue = t every frame;
        // without this guard, the setter calls SeekAllToProportionalTime
        // → master.Seek(t) → ReplayTimeline.Seek(t) resets
        // _playStartTimestamp = t (line 181). Effect: PlayedTimestamp
        // = t + elapsed_after_seek where t = frame.ts of the emit, so
        // every tick "advances" by elapsed but t also advances by
        // ~0.1s per emit. Net: cursor snaps to trace-end on the first
        // emit, then continues at trace-end in a fast-forward loop
        // (5000 frames in 0.013s observed in production). User
        // symptom: "progress bar jumps straight to end" — exactly the
        // v3.16.3 PATCH-introduced regression.
        //
        // Guard: when master is actively playing, the ScrubberValue
        // setter writes are writebacks from FrameEmitted, not user
        // input. Skip the seek in that case. User drag is unaffected
        // (master is not playing during drag because Pause would be
        // hit first, or IsPlaying=false).
        if (_masterService is null) return;
        if (_masterService.State == ReplayState.Playing) return;
        if (TotalDuration > 0)
            SeekAllToProportionalTime(value);
    }

    partial void OnLoopChanged(bool value)
    {
        PropagateLoopToAllServices();
    }

    partial void OnSpeedChanged(double value)
    {
        PropagateSpeedToAllServices();
    }
}