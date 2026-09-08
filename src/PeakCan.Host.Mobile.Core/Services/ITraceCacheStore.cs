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
}
