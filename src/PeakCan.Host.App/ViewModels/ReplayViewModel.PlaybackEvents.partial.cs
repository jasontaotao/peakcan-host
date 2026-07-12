using System.ComponentModel;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class ReplayViewModel
{
    // Flow B: PlaybackEvents (v1.4.0 MINOR Task 4 + v1.4.2 PATCH Item 3 + v3.7.0 MINOR Chunk 2 + v3.9.2 PATCH H1 + v3.14.0 MINOR A2/A3 + earlier).
    // 5 IReplayService event handlers (FrameEmitted / PlaybackEnded / LoopRewound + 2 derived)
    // + 1 IDisposable.Dispose + 1 RecentSessionsService INPC handler.
    // All share SynchronizationContext-marshalling pattern (event arrives on timer thread,
    // posted to UI thread) plus Dispose unsubscribes the same handlers. Sister of W14 D2
    // lifecycle-cluster pattern (lifecycle primitives stay coupled in one partial).
    //
    // Cross-flow callers (partial-class visible):
    //   - OnFrameEmitted/OnPlaybackEnded/OnLoopRewound reassign CurrentTimestamp/ErrorMessage/StatusMessage
    //     ([ObservableProperty] fields in main, CommunityToolkit.Mvvm source generator emits setters)
    //   - OnPlaybackEnded invokes IsPlaying ([ObservableProperty] field in main)
    //   - ApplyPlaybackEnded reads e.Error (PlaybackEndedEventArgs arg)
    //   - Dispose unsubscribes _service.LoopRewound/FrameEmitted/PlaybackEnded + _recentSessions.PropertyChanged
    //     (all 3 fields are in main, partial-class visible)

    /// <summary>
    /// v3.14.0 MINOR A3: handler for <see cref="RecentSessionsService"/>'s
    /// INPC. Promoted from a lambda in the ctor so Dispose can cancel
    /// the subscription. Lambdas are not referenceable from -= and
    /// would otherwise pin this VM to the singleton's lifetime.
    /// </summary>
    private void OnRecentSessionsPropertyChanged(object? sender, PropertyChangedEventArgs e)
        => RefreshRecentEntries();

    /// <summary>
    /// FrameEmitted is invoked on the timer callback thread. We Post the
    /// timestamp update to the captured <see cref="SynchronizationContext"/>
    /// so the binding writes occur on the UI thread. Without this, WPF
    /// throws on cross-thread collection / DP access.
    /// </summary>
    private void OnFrameEmitted(ReplayFrame frame)
    {
        if (_syncContext is not null)
        {
            _syncContext.Post(_ => CurrentTimestamp = frame.Timestamp, null);
        }
        else
        {
            // Test path: no SynchronizationContext. Direct set is safe
            // because tests don't pump the dispatcher — they assert on
            // the value immediately after raising the event.
            CurrentTimestamp = frame.Timestamp;
        }
    }

    /// <summary>
    /// v1.4.2 PATCH Item 3: invoked on the timer callback thread when
    /// playback ends (EOF or sink failure). Surfaces sink failures in
    /// <see cref="ErrorMessage"/>; on normal EOF, just resets
    /// <see cref="IsPlaying"/>. Marshals to the captured
    /// <see cref="SynchronizationContext"/> like <see cref="OnFrameEmitted"/>.
    /// </summary>
    private void OnPlaybackEnded(object? sender, PlaybackEndedEventArgs e)
    {
        if (_syncContext is not null)
        {
            _syncContext.Post(_ => ApplyPlaybackEnded(e), null);
        }
        else
        {
            // Test path: no SynchronizationContext. Direct call is safe
            // because tests assert on the state immediately after the event.
            ApplyPlaybackEnded(e);
        }
    }

    private void ApplyPlaybackEnded(PlaybackEndedEventArgs e)
    {
        if (e.Error is not null)
        {
            ErrorMessage = $"Replay aborted: {e.Error.Message}";
        }
        // Whether the end was normal (EOF) or error, stop playing.
        IsPlaying = false;
    }

    /// <summary>
    /// v3.9.2 PATCH H1: handler for
    /// <see cref="IReplayService.LoopRewound"/>. Surfaced via
    /// <see cref="StatusMessage"/> so the user sees the rewind happen
    /// during A/B loop playback. Fired on the timer-callback thread —
    /// marshal to <see cref="SynchronizationContext"/> like the
    /// sibling <see cref="OnFrameEmitted"/> / <see cref="OnPlaybackEnded"/>
    /// handlers.
    /// </summary>
    private void OnLoopRewound(object? sender, LoopRegionRewoundEventArgs e)
    {
        if (_syncContext is not null)
        {
            _syncContext.Post(_ => StatusMessage = $"Rewind: loop region ({e.Start:F2}s - {e.End:F2}s)", null);
        }
        else
        {
            // Test path: no SynchronizationContext. Direct call is safe
            // because tests assert on the state immediately after the event.
            StatusMessage = $"Rewind: loop region ({e.Start:F2}s - {e.End:F2}s)";
        }
    }

    /// <summary>
    /// Unsubscribe from <see cref="IReplayService.FrameEmitted"/> and
    /// stop playback. Safe to call multiple times - the service is
    /// thread-safe and <see cref="ReplayService.Stop"/> is idempotent.
    /// <para>
    /// v3.14.0 MINOR A2: cancel the v3.9.0 MINOR P1 LoopRewound subscription.
    /// <see cref="IReplayService"/> is a DI singleton, so without the -=
    /// the closure chain singleton -> old-VM -> old-frames prevents
    /// old-VM GC.
    /// </para>
    /// <para>
    /// v3.14.0 MINOR A3: cancel the <see cref="RecentSessionsService"/>
    /// PropertyChanged subscription. The lambda in the ctor was promoted
    /// to <see cref="OnRecentSessionsPropertyChanged"/> so Dispose can
    /// -= it (lambdas can't be -=ed by reference). RecentSessionsService
    /// is a DI singleton so without the -= the closure chain pins the VM.
    /// </para>
    /// </summary>
    public void Dispose()
    {
        _service.LoopRewound -= OnLoopRewound;
        _service.FrameEmitted -= OnFrameEmitted;
        _service.PlaybackEnded -= OnPlaybackEnded;
        // v3.14.0 MINOR A3: cancel the RecentSessionsService.PropertyChanged
        // subscription. Matches the += in the ctor (promoted from lambda).
        _recentSessions.PropertyChanged -= OnRecentSessionsPropertyChanged;
        _service.Stop();
        GC.SuppressFinalize(this);
    }
}
