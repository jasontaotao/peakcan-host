namespace PeakCan.Host.Core.Replay;

/// <summary>
/// v1.4.2 PATCH Item 3: event args for <see cref="IReplayService.PlaybackEnded"/>.
/// <see cref="Error"/> is set when playback was aborted due to a sink
/// failure (e.g. <see cref="ReplaySendException"/>); <c>null</c> when
/// playback reached EOF normally or was stopped by the user.
/// </summary>
public class PlaybackEndedEventArgs : EventArgs
{
    /// <summary>
    /// Exception that aborted playback, or <c>null</c> if playback
    /// ended normally (EOF) or was stopped by the user.
    /// </summary>
    public Exception? Error { get; }

    public PlaybackEndedEventArgs(Exception? error = null)
    {
        Error = error;
    }
}
