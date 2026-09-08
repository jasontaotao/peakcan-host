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

/// <summary>Keyset paged cache query. Forward paging uses AfterIndex; backward paging uses BeforeIndex.</summary>
public sealed record FrameQuery(
    long? AfterIndex = null,
    long? BeforeIndex = null,
    IReadOnlySet<uint>? CanIds = null,
    int Limit = 80);

/// <summary>One cache page. HasMore is true when Limit+1 rows were available.</summary>
public sealed record FramePage(IReadOnlyList<CachedFrame> Frames, bool HasMore);