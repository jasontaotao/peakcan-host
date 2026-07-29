namespace PeakCan.Host.Core.HIL.Diff;

/// <summary>
/// Frame alignment strategy for diff comparison.
/// </summary>
public enum AlignStrategy
{
    /// <summary>Align frames by timestamp proximity.</summary>
    Timestamp,

    /// <summary>Align by nearest neighbor within a time window.</summary>
    NearestNeighbor,

    /// <summary>Align by frame index (ignore timestamps).</summary>
    Index,
}
