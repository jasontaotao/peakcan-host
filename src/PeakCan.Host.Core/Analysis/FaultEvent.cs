namespace PeakCan.Host.Core.Analysis;

/// <summary>v3.52.0 MINOR: a user-identified fault moment.
/// CenterTimestampSeconds is in seconds-from-recording-start (matches
/// ReplayFrame.Timestamp unit). Default window is ±500ms per spec D3.</summary>
public sealed record FaultEvent(
    double CenterTimestampSeconds,
    TimeSpan WindowBefore,
    TimeSpan WindowAfter,
    string Description,
    DateTime CreatedAtUtc);