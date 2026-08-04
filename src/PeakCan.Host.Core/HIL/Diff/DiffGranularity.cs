namespace PeakCan.HIL.Core.HIL.Diff;

/// <summary>
/// Granularity level for diff comparison.
/// </summary>
public enum DiffGranularity
{
    /// <summary>Frame-level exact match (ID + Data).</summary>
    Frame,

    /// <summary>Signal-level tolerance match (physical value ±tolerance).</summary>
    Signal,

    /// <summary>Event-level window match (frame appears within time window, timing insensitive).</summary>
    Event,
}
