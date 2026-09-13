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
    public FrameRow ToFrameRow(DbcCatalog? dbc = null) => FrameRow.FromCached(this, dbc);
}

/// <summary>Cache search direction for <see cref="ITraceCacheStore.FindFrameAsync"/>.</summary>
public enum CacheSearchDirection : byte { First, Next }

/// <summary>
/// Keyset paged cache query. Forward paging uses AfterIndex; backward paging uses BeforeIndex.
/// <para>Filter sets follow the <c>CanIdListParser</c> tri-state: <c>null</c> = no
/// filter, empty set = all-invalid input (reject all), populated = allow-list.
/// CanIds pushes down via <c>can_id</c>, PgnAllowList via the <c>pgn</c> generated
/// column (extended frames only); when both are set the page is a merge of the
/// two index-perfect branches (OR semantics).</para>
/// </summary>
public sealed record FrameQuery(
    long? AfterIndex = null,
    long? BeforeIndex = null,
    IReadOnlySet<uint>? CanIds = null,
    IReadOnlySet<uint>? PgnAllowList = null,
    int Limit = 80);

/// <summary>One cache page. HasMore is true when Limit+1 rows were available.</summary>
public sealed record FramePage(IReadOnlyList<CachedFrame> Frames, bool HasMore);