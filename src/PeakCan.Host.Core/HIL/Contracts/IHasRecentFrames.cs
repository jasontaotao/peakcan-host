namespace PeakCan.HIL.Core.HIL.Contracts;

/// <summary>
/// Implemented by assertion contexts that maintain a recent-frames ring buffer.
/// Used by TestSuiteEngine to capture FramesAroundFailure on step failure.
/// </summary>
public interface IHasRecentFrames
{
    /// <summary>Snapshot of the ring buffer (copy). Thread-safe.</summary>
    IReadOnlyList<CanFrame> GetRecentFrames();
}
