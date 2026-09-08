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
    /// <summary>skipUntil: 只产出 timestamp >= skipUntil 的帧；之前的行仅做轻量时间戳检查。</summary>
    Task<StreamingTraceOpenResult> OpenAsync(double? skipUntil = null, CancellationToken ct = default);
}

