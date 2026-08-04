namespace PeakCan.HIL.Core.HIL.Diff;

/// <summary>
/// Single diff entry describing one difference between golden and actual sequences.
/// </summary>
public sealed record DiffEntry(
    DiffEntryType Type,
    int? GoldenIndex,
    int? ActualIndex,
    string? Reason,
    CanFrame? GoldenFrame,
    CanFrame? ActualFrame);

/// <summary>
/// Type of diff entry.
/// </summary>
public enum DiffEntryType { Added, Removed, Modified, Matched }
