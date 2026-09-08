using System.Threading.Channels;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.Mobile.Core.Services;

/// <summary>
/// Buffers player emissions and appends SQLite batches in the background.
/// The bounded queue drops its oldest item when full so a slow disk cannot
/// block playback; any drop or write failure leaves the trace incomplete.
/// </summary>
public sealed class TraceCacheWriter : ITraceCacheSink
{
    public const int BatchSize = 5000;
    private const int QueueCapacity = 16384;

    private readonly ITraceCacheStore _store;
    private readonly long _traceId;
    private readonly Channel<ReplayFrame> _channel = Channel.CreateBounded<ReplayFrame>(
        new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    private readonly Task _pump;
    private readonly object _enqueueGate = new();
    private long _nextIndex;
    private long _written;
    private long _dropped;
    private double _lastTimestamp;
    private long _failed;
    private int _closed;

    // Deterministic synchronization points for the concurrency regression test.
    internal Func<Task>? EnqueueGateEnteredForTests { get; set; }
    internal Action? CloseStartedForTests { get; set; }

    private TraceCacheWriter(ITraceCacheStore store, long traceId)
    {
        _store = store;
        _traceId = traceId;
        _pump = PumpAsync();
    }

    public static TraceCacheWriter CreateForTests(ITraceCacheStore store, long traceId) => new(store, traceId);

    public long TraceId => _traceId;
    public bool IsEnabled => Interlocked.Read(ref _failed) == 0;
    public long WrittenFrames => Interlocked.Read(ref _written);
    public long DroppedFrames => Interlocked.Read(ref _dropped);
    public Exception? Failure { get; private set; }

    public void Enqueue(ReplayFrame frame)
    {
        if (!IsEnabled || Volatile.Read(ref _closed) != 0) return;

        lock (_enqueueGate)
        {
            if (!IsEnabled || Volatile.Read(ref _closed) != 0) return;

            if (EnqueueGateEnteredForTests is not null)
                EnqueueGateEnteredForTests().GetAwaiter().GetResult();

            // DropOldest is performed explicitly so the drop metric stays accurate.
            if (!_channel.Writer.TryWrite(frame))
            {
                if (_channel.Reader.TryRead(out _))
                    Interlocked.Increment(ref _dropped);

                if (!_channel.Writer.TryWrite(frame))
                    Interlocked.Increment(ref _dropped);
            }
        }
    }

    public async Task CloseAsync(bool markComplete, CancellationToken ct = default)
    {
        CloseStartedForTests?.Invoke();
        lock (_enqueueGate)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            _channel.Writer.TryComplete();
        }
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Failure ??= ex;
            Interlocked.Exchange(ref _failed, 1);
        }

        if (Failure is null && _lastTimestamp > 0)
            await _store.UpdateLastPositionAsync(_traceId, _lastTimestamp, ct).ConfigureAwait(false);

        if (markComplete && Failure is null && Interlocked.Read(ref _dropped) == 0)
            await _store.MarkCompletedAsync(_traceId, ct).ConfigureAwait(false);
    }

    private async Task PumpAsync()
    {
        var batch = new List<CachedFrame>(BatchSize);
        await foreach (var frame in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            batch.Add(new CachedFrame(
                Interlocked.Increment(ref _nextIndex) - 1,
                frame.Timestamp,
                frame.Id,
                frame.IsExtended,
                frame.Dlc,
                frame.Data));
            _lastTimestamp = Math.Max(_lastTimestamp, frame.Timestamp);
            Interlocked.Increment(ref _written);

            if (batch.Count >= BatchSize)
            {
                await FlushAsync(batch).ConfigureAwait(false);
                if (!IsEnabled) return;
            }
        }

        if (batch.Count > 0 && IsEnabled)
            await FlushAsync(batch).ConfigureAwait(false);
    }

    private async Task FlushAsync(List<CachedFrame> batch)
    {
        try
        {
            await _store.AppendFramesAsync(_traceId, batch).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Failure = ex;
            Interlocked.Exchange(ref _failed, 1);
            _channel.Writer.TryComplete();
        }
        finally
        {
            batch.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _closed) == 0)
            await CloseAsync(markComplete: false).ConfigureAwait(false);
    }
}

/// <summary>Default cache sink factory using <see cref="TraceCacheStore"/>.</summary>
public sealed class TraceCacheWriterFactory(ITraceCacheStore store) : ITraceCacheSinkFactory
{
    public async Task<ITraceCacheSink?> StartAsync(string sourceName, long fileSizeBytes, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceName);
        try
        {
            await store.InitializeAsync(ct).ConfigureAwait(false);
            if (await store.FindCompletedAsync(sourceName, fileSizeBytes, ct).ConfigureAwait(false) is not null)
                return null;

            var traceId = await store.GetOrCreateTraceAsync(sourceName, fileSizeBytes, ct).ConfigureAwait(false);
            return TraceCacheWriter.CreateForTests(store, traceId);
        }
        catch
        {
            return null;
        }
    }
}
