using FluentAssertions;
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Mobile.Core.Services;
using Xunit;

namespace PeakCan.Host.Mobile.Core.Tests.Services;

public class TraceCacheWriterTests
{
    private const int QueueCapacity = 16384;

    private static ReplayFrame Frame(double timestamp, uint id = 0x100) =>
        new(timestamp, id, 2, [1, 2], default, false);

    private sealed class FakeStore : ITraceCacheStore
    {
        public List<CachedFrame[]> Batches { get; } = [];
        public List<long> Completed { get; } = [];
        public int InitializeCount { get; private set; }
        public Func<CachedFrame[], Task>? AppendOverride { get; set; }
        public TraceCacheSummary? CompletedLookup { get; set; }

        public Task InitializeAsync(CancellationToken ct = default)
        {
            InitializeCount++;
            return Task.CompletedTask;
        }

        public Task<long> GetOrCreateTraceAsync(string sourceName, long fileSizeBytes, CancellationToken ct = default) => Task.FromResult(42L);

        public async Task AppendFramesAsync(long traceId, IReadOnlyList<CachedFrame> frames, CancellationToken ct = default)
        {
            var copy = frames.ToArray();
            Batches.Add(copy);
            if (AppendOverride is not null) await AppendOverride(copy);
        }

        public Task MarkCompletedAsync(long traceId, CancellationToken ct = default) { Completed.Add(traceId); return Task.CompletedTask; }
        public Task UpdateLastPositionAsync(long traceId, double seconds, CancellationToken ct = default) => Task.CompletedTask;
        public Task<TraceCacheSummary?> FindCompletedAsync(string sourceName, long fileSizeBytes, CancellationToken ct = default) => Task.FromResult(CompletedLookup);
        public Task<TraceCacheSummary?> GetTraceAsync(long traceId, CancellationToken ct = default) => Task.FromResult<TraceCacheSummary?>(null);
        public Task<FramePage> GetFramesAsync(long traceId, FrameQuery query, CancellationToken ct = default) => Task.FromResult(new FramePage([], false));
        public Task<IReadOnlyList<TraceCacheSummary>> ListTracesAsync(int limit = 100, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TraceCacheSummary>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Close_Flushes_Batches_And_Marks_Complete()
    {
        var store = new FakeStore();
        await using var writer = TraceCacheWriter.CreateForTests(store, 42);

        for (var i = 0; i < 5000; i++) writer.Enqueue(Frame(i * 0.001));
        await writer.CloseAsync(markComplete: true);

        store.Batches.Should().ContainSingle(b => b.Length == 5000);
        store.Completed.Should().ContainSingle(t => t == 42);
        writer.WrittenFrames.Should().Be(5000);
        writer.DroppedFrames.Should().Be(0);
    }

    [Fact]
    public async Task Close_Without_Complete_Does_Not_Mark_Complete()
    {
        var store = new FakeStore();
        await using var writer = TraceCacheWriter.CreateForTests(store, 42);

        writer.Enqueue(Frame(1));
        await writer.CloseAsync(markComplete: false);

        writer.WrittenFrames.Should().Be(1);
        store.Completed.Should().BeEmpty();
    }

    [Fact]
    public async Task Queue_Overflow_Drops_Oldest_And_Does_Not_Mark_Complete()
    {
        var store = new FakeStore();
        var releaseAppend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.AppendOverride = _ => releaseAppend.Task;
        await using var writer = TraceCacheWriter.CreateForTests(store, 42);

        for (var i = 0; i < 25000; i++) writer.Enqueue(Frame(i * 0.001, (uint)i));
        releaseAppend.SetResult();
        await writer.CloseAsync(markComplete: true);

        var dropped = writer.DroppedFrames;
        dropped.Should().BePositive();
        writer.WrittenFrames.Should().Be(25000 - dropped);
        store.Batches.SelectMany(b => b).Should().HaveCount((int)writer.WrittenFrames);

        // The first 5000 frames were already flushed; overflow drops the next oldest frames.
        store.Batches.SelectMany(b => b).Should().NotContain(f => f.CanId == TraceCacheWriter.BatchSize);
        store.Batches.SelectMany(b => b).Should().Contain(f => f.CanId == TraceCacheWriter.BatchSize + dropped);
        store.Completed.Should().BeEmpty();
    }

    [Fact]
    public async Task Append_Failure_Disables_Sink_And_Does_Not_Mark_Complete()
    {
        var store = new FakeStore
        {
            AppendOverride = _ => throw new IOException("disk full")
        };
        await using var writer = TraceCacheWriter.CreateForTests(store, 42);

        writer.Enqueue(Frame(1));
        await writer.CloseAsync(markComplete: true);

        writer.IsEnabled.Should().BeFalse();
        writer.Failure.Should().BeAssignableTo<IOException>();
        store.Completed.Should().BeEmpty();
    }

    [Fact]
    public async Task Close_Does_Not_Count_Frame_Enqueued_During_Close_Race()
    {
        var store = new FakeStore();
        await using var writer = TraceCacheWriter.CreateForTests(store, 42);
        var enqueueGateEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseEnqueue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        writer.EnqueueGateEnteredForTests = () =>
        {
            enqueueGateEntered.TrySetResult();
            return releaseEnqueue.Task;
        };
        writer.CloseStartedForTests = () => closeStarted.TrySetResult();

        var enqueueTask = Task.Run(() => writer.Enqueue(Frame(1)));
        await enqueueGateEntered.Task;
        var closeTask = Task.Run(() => writer.CloseAsync(markComplete: true));
        await closeStarted.Task;

        releaseEnqueue.TrySetResult();
        await enqueueTask;
        await closeTask;

        writer.WrittenFrames.Should().Be(1);
        writer.DroppedFrames.Should().Be(0);
        store.Batches.Should().ContainSingle(b => b.Length == 1);
        store.Completed.Should().ContainSingle(t => t == 42);
    }

    [Fact]
    public async Task Factory_Starts_Writer_After_Initializing_Store()
    {
        var store = new FakeStore();
        await using var sink = await new TraceCacheWriterFactory(store).StartAsync("trace.asc", 100);

        sink.Should().NotBeNull();
        sink!.TraceId.Should().Be(42);
        sink.IsEnabled.Should().BeTrue();
        store.InitializeCount.Should().Be(1);
    }

    [Fact]
    public async Task Factory_Returns_Null_When_Completed_Cache_Exists()
    {
        var store = new FakeStore
        {
            CompletedLookup = new TraceCacheSummary(7, "trace.asc", 100, DateTimeOffset.UtcNow, 1, 1, true, 1)
        };
        await using var sink = await new TraceCacheWriterFactory(store).StartAsync("trace.asc", 100);

        sink.Should().BeNull();
        store.InitializeCount.Should().Be(1);
    }

    [Fact]
    public async Task Factory_Returns_Null_When_Store_Is_Unavailable()
    {
        var store = new ThrowingStore();
        await using var sink = await new TraceCacheWriterFactory(store).StartAsync("trace.asc", 100);

        sink.Should().BeNull();
    }

    private sealed class ThrowingStore : ITraceCacheStore
    {
        public Task InitializeAsync(CancellationToken ct = default) => throw new IOException("cache unavailable");
        public Task<long> GetOrCreateTraceAsync(string sourceName, long fileSizeBytes, CancellationToken ct = default) => Task.FromResult(42L);
        public Task AppendFramesAsync(long traceId, IReadOnlyList<CachedFrame> frames, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkCompletedAsync(long traceId, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateLastPositionAsync(long traceId, double seconds, CancellationToken ct = default) => Task.CompletedTask;
        public Task<TraceCacheSummary?> FindCompletedAsync(string sourceName, long fileSizeBytes, CancellationToken ct = default) => Task.FromResult<TraceCacheSummary?>(null);
        public Task<TraceCacheSummary?> GetTraceAsync(long traceId, CancellationToken ct = default) => Task.FromResult<TraceCacheSummary?>(null);
        public Task<FramePage> GetFramesAsync(long traceId, FrameQuery query, CancellationToken ct = default) => Task.FromResult(new FramePage([], false));
        public Task<IReadOnlyList<TraceCacheSummary>> ListTracesAsync(int limit = 100, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TraceCacheSummary>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
