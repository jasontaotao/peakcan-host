namespace PeakCan.Host.Mobile.Core.Services;

/// <summary>Persistent replay cache. Implementations must be safe for serialized async use.</summary>
public interface ITraceCacheStore : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<long> GetOrCreateTraceAsync(string sourceName, long fileSizeBytes, CancellationToken ct = default);
    Task AppendFramesAsync(long traceId, IReadOnlyList<CachedFrame> frames, CancellationToken ct = default);
    Task MarkCompletedAsync(long traceId, CancellationToken ct = default);
    Task UpdateLastPositionAsync(long traceId, double seconds, CancellationToken ct = default);
    Task<TraceCacheSummary?> FindCompletedAsync(string sourceName, long fileSizeBytes, CancellationToken ct = default);
    Task<TraceCacheSummary?> GetTraceAsync(long traceId, CancellationToken ct = default);
    Task<FramePage> GetFramesAsync(long traceId, FrameQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<TraceCacheSummary>> ListTracesAsync(int limit = 100, CancellationToken ct = default);

    /// <summary>Returns the latest cached frame of each CAN ID at or before the given timestamp (per ID, by its largest idx), ordered by can_id ascending. Empty when no frame precedes the timestamp.</summary>
    Task<IReadOnlyList<CachedFrame>> GetLatestFramesBeforeAsync(
        long traceId, double timestamp, CancellationToken ct = default);

    /// <summary>Finds a cached frame of one CAN ID: First=earliest frame overall; Next=earliest frame strictly after afterTimestamp. Returns null when no match.</summary>
    Task<CachedFrame?> FindFrameAsync(
        long traceId, uint canId, double? afterTimestamp,
        CacheSearchDirection direction, CancellationToken ct = default);

    /// <summary>
    /// Window query for one CAN ID over the cached region: closed interval
    /// [<paramref name="tStart"/>, <paramref name="tEnd"/>] (null = open-ended),
    /// ordered by timestamp then idx. Returns at most <paramref name="limit"/>
    /// rows; <see cref="FramePage.HasMore"/> reports truncation (read as
    /// <c>truncated</c> by the chat tool — the raw window exceeded the budget).
    /// Uses the idx_frames_cid_ts covering index.
    /// </summary>
    Task<FramePage> GetFramesForCanIdAsync(
        long traceId, uint canId, double? tStart, double? tEnd,
        int limit = 20000, CancellationToken ct = default);
}
