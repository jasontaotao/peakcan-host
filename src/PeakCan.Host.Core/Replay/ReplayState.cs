namespace PeakCan.Host.Core.Replay;

/// <summary>Playback state of <see cref="IReplayService"/>.</summary>
public enum ReplayState
{
    /// <summary>No file loaded or playback stopped.</summary>
    Stopped,
    /// <summary>Frames are being emitted at scheduled timestamps.</summary>
    Playing,
    /// <summary>Playback halted; can be resumed from <see cref="IReplayService.CurrentTimestamp"/>.</summary>
    Paused
}
