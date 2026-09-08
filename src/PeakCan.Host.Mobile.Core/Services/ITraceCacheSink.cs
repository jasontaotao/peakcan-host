using System.Threading.Channels;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.Mobile.Core.Services;

/// <summary>Non-blocking cache target fed from player frame events.</summary>
public interface ITraceCacheSink : IAsyncDisposable
{
    long TraceId { get; }
    bool IsEnabled { get; }
    long WrittenFrames { get; }
    long DroppedFrames { get; }
    Exception? Failure { get; }

    void Enqueue(ReplayFrame frame);

    /// <summary>Drain queued frames. <paramref name="markComplete"/> is true only for clean EOF.</summary>
    Task<bool> CloseAsync(bool markComplete, CancellationToken ct = default);
}

/// <summary>Creates a cache sink for one trace playback session.</summary>
public interface ITraceCacheSinkFactory
{
    /// <summary>Returns null when cache is unavailable or the trace is already complete.</summary>
    Task<ITraceCacheSink?> StartAsync(string sourceName, long fileSizeBytes, CancellationToken ct = default);
}
