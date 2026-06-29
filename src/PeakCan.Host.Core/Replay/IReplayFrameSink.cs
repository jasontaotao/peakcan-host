namespace PeakCan.Host.Core.Replay;

/// <summary>
/// Receives frames emitted by <see cref="IReplayService"/>. DI-injected
/// so tests can substitute a fake sink to capture emitted frames without
/// a live bus.
/// </summary>
public interface IReplayFrameSink
{
    /// <summary>Send a replay frame (typically to the bus via ChannelRouter).</summary>
    ValueTask SendFrameAsync(ReplayFrame frame, CancellationToken ct = default);
}
