using PeakCan.Host.Mobile.Core.Models;

namespace PeakCan.Host.Mobile.Core.Services;

/// <summary>Metadata for one cached trace file.</summary>
public sealed record TraceCacheSummary(
    long TraceId,
    string SourceName,
    long FileSizeBytes,
    DateTimeOffset ImportedAt,
    long FrameCount,
    double Duration,
    bool Complete,
    double LastPositionSeconds);

/// <summary>One frame stored in the replay cache.</summary>
public sealed record CachedFrame(
    long Index,
    double Timestamp,
    uint CanId,
    bool IsExtended,
    byte Dlc,
    byte[] Data)
{
    public FrameRow ToFrameRow() =>
        new(Timestamp, CanId, IsExtended, Dlc, Data);
}
