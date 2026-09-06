namespace PeakCan.Host.Core.Replay;

/// <summary>
/// v3.9.0 MINOR P1: event args for <see cref="IReplayService.LoopRewound"/>.
/// Carries the active loop region's bounds so the UI can log
/// "Rewind: loop region X" and reset visual state without having to
/// re-query <see cref="IReplayService.ActiveLoopRegion"/> (which may
/// have changed by the time the handler runs on the UI thread).
/// </summary>
public class LoopRegionRewoundEventArgs : EventArgs
{
    /// <summary>Region's lower bound (seconds from recording start).</summary>
    public double Start { get; }

    /// <summary>Region's upper bound (seconds from recording start).</summary>
    public double End { get; }

    public LoopRegionRewoundEventArgs(double start, double end)
    {
        Start = start;
        End = end;
    }
}
