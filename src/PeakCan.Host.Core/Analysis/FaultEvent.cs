using System;

namespace PeakCan.Host.Core.Analysis;

/// <summary>v3.52.0 MINOR: a user-identified fault moment.
/// CenterTimestampSeconds is in seconds-from-recording-start (matches
/// ReplayFrame.Timestamp unit). Default window is ±500ms per spec D3.
/// v3.61.0 PATCH OPT-019: validates WindowBefore/After >= 0 to prevent
/// empty frame filter results from negative windows.</summary>
public sealed record FaultEvent
{
    public double CenterTimestampSeconds { get; }
    public TimeSpan WindowBefore { get; }
    public TimeSpan WindowAfter { get; }
    public string Description { get; }
    public DateTime CreatedAtUtc { get; }

    public FaultEvent(
        double CenterTimestampSeconds,
        TimeSpan WindowBefore,
        TimeSpan WindowAfter,
        string Description,
        DateTime CreatedAtUtc)
    {
        if (WindowBefore < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(WindowBefore), "WindowBefore must be non-negative");
        if (WindowAfter < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(WindowAfter), "WindowAfter must be non-negative");
        this.CenterTimestampSeconds = CenterTimestampSeconds;
        this.WindowBefore = WindowBefore;
        this.WindowAfter = WindowAfter;
        this.Description = Description ?? throw new ArgumentNullException(nameof(Description));
        this.CreatedAtUtc = CreatedAtUtc;
    }
}