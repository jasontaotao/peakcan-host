namespace PeakCan.Host.Core.Replay;

/// <summary>
/// Streaming parse counters, live during enumeration. Thread-safe reads
/// (Interlocked) because the consumer reads from a different thread than
/// the producer (parser on a background task, UI/player reading SkippedLines).
/// </summary>
public sealed class StreamingParseStats
{
    private long _skippedLines;
    private long _bytesRead;

    public long SkippedLines => Interlocked.Read(ref _skippedLines);
    public long BytesRead => Interlocked.Read(ref _bytesRead);

    internal void IncSkipped() => Interlocked.Increment(ref _skippedLines);
    internal void AddBytes(long n)
    {
        if (n > 0) Interlocked.Add(ref _bytesRead, n);
    }
}
