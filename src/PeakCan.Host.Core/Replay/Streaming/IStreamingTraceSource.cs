namespace PeakCan.Host.Core.Replay;

/// <summary>
/// Re-openable source of a streaming frame enumeration. Each
/// <see cref="OpenAsync"/> returns a fresh <see cref="StreamingTraceOpenResult"/>
/// whose <c>Frames</c> starts at the beginning of the source (or where the
/// concrete impl positions it). Used by <see cref="StreamingTracePlayer"/>
/// for fast-forward Seek.
/// </summary>
public interface IStreamingTraceSource
{
    Task<StreamingTraceOpenResult> OpenAsync(CancellationToken ct = default);
}
