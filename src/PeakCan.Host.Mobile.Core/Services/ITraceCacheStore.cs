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
}
