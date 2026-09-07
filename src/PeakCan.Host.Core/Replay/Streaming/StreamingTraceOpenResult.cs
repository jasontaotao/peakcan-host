namespace PeakCan.Host.Core.Replay;

/// <summary>
/// One opened streaming parse session. Header is populated eagerly during
/// <see cref="IStreamingTraceSource.OpenAsync"/>; <see cref="Frames"/> is
/// lazily enumerated. Re-open (e.g. for seek) by calling <c>OpenAsync</c> again.
/// </summary>
public sealed class StreamingTraceOpenResult : IAsyncDisposable
{
    public DateTime? WallClockOrigin { get; init; }
    public bool TimestampsAreAbsolute { get; init; }
    public required IAsyncEnumerable<ReplayFrame> Frames { get; init; }
    public long? SourceLengthBytes { get; init; }
    public required StreamingParseStats Stats { get; init; }
    public Stream? SourceStream { get; init; }

    public async ValueTask DisposeAsync()
    {
        if (SourceStream is not null)
            await SourceStream.DisposeAsync().ConfigureAwait(false);
    }
}
