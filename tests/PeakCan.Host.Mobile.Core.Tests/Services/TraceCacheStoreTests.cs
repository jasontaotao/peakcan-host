using FluentAssertions;
using PeakCan.Host.Mobile.Core.Services;
using Xunit;

namespace PeakCan.Host.Mobile.Core.Tests.Services;

public class TraceCacheStoreTests
{
    private static CachedFrame Frame(long index, double timestamp, uint id = 0x100)
        => new(index, timestamp, id, false, 2, [1, 2]);

    [Fact]
    public async Task Initialize_Is_Idempotent_And_Creates_Trace()
    {
        await using var store = new TraceCacheStore(":memory:");
        await store.InitializeAsync();

        var id = await store.GetOrCreateTraceAsync("a.asc", 100);
        await store.InitializeAsync();

        id.Should().BeGreaterThan(0);
        var summary = await store.GetTraceAsync(id);
        summary!.SourceName.Should().Be("a.asc");
        summary.FileSizeBytes.Should().Be(100);
        summary.Complete.Should().BeFalse();
    }

    [Fact]
    public async Task GetOrCreate_Reuses_Same_Name_And_Size()
    {
        await using var store = new TraceCacheStore(":memory:");
        var first = await store.GetOrCreateTraceAsync("a.asc", 100);
        var second = await store.GetOrCreateTraceAsync("a.asc", 100);
        var other = await store.GetOrCreateTraceAsync("a.asc", 101);

        second.Should().Be(first);
        other.Should().NotBe(first);
    }

    [Fact]
    public async Task AppendFrames_Updates_FrameCount_And_Duration()
    {
        await using var store = new TraceCacheStore(":memory:");
        var id = await store.GetOrCreateTraceAsync("a.asc", 100);

        await store.AppendFramesAsync(id, [Frame(0, 0), Frame(1, 0.5)]);
        await store.AppendFramesAsync(id, [Frame(2, 1.25)]);

        var summary = await store.GetTraceAsync(id);
        summary!.FrameCount.Should().Be(3);
        summary.Duration.Should().Be(1.25);
    }

    [Fact]
    public async Task Completed_Trace_Can_Be_Found_By_Name_And_Size()
    {
        await using var store = new TraceCacheStore(":memory:");
        var id = await store.GetOrCreateTraceAsync("a.asc", 100);
        (await store.FindCompletedAsync("a.asc", 100)).Should().BeNull();

        await store.MarkCompletedAsync(id);
        await store.UpdateLastPositionAsync(id, 12.5);

        var found = await store.FindCompletedAsync("a.asc", 100);
        found!.TraceId.Should().Be(id);
        found.Complete.Should().BeTrue();
        found.LastPositionSeconds.Should().Be(12.5);
    }

    [Fact]
    public async Task GetFrames_Pages_Forward_And_Filters_CanIds()
    {
        await using var store = new TraceCacheStore(":memory:");
        var id = await store.GetOrCreateTraceAsync("a.asc", 100);
        var frames = Enumerable.Range(0, 181)
            .Select(i => Frame(i, i * 0.01, i % 2 == 0 ? 0x100u : 0x200u))
            .ToArray();
        await store.AppendFramesAsync(id, frames);

        var first = await store.GetFramesAsync(id, new FrameQuery(AfterIndex: -1, Limit: 80));
        first.Frames.Should().HaveCount(80);
        first.Frames[0].Index.Should().Be(0);
        first.Frames[^1].Index.Should().Be(79);
        first.HasMore.Should().BeTrue();

        var filtered = await store.GetFramesAsync(id, new FrameQuery(AfterIndex: -1, CanIds: new HashSet<uint> { 0x200 }, Limit: 3));
        filtered.Frames.Select(f => f.Index).Should().Equal([1L, 3L, 5L]);
        filtered.HasMore.Should().BeTrue();
    }

    [Fact]
    public async Task GetFrames_Pages_Backward_In_Chronological_Order()
    {
        await using var store = new TraceCacheStore(":memory:");
        var id = await store.GetOrCreateTraceAsync("a.asc", 100);
        var frames = Enumerable.Range(0, 100).Select(i => Frame(i, i)).ToArray();
        await store.AppendFramesAsync(id, frames);

        var previous = await store.GetFramesAsync(id, new FrameQuery(BeforeIndex: 80, Limit: 50));

        previous.Frames.Select(f => f.Index).Should().Equal(Enumerable.Range(30, 50).Select(i => (long)i));
        previous.HasMore.Should().BeTrue();
    }

    [Fact]
    public async Task ListTraces_Returns_Newest_First()
    {
        await using var store = new TraceCacheStore(":memory:");
        var first = await store.GetOrCreateTraceAsync("first.asc", 1);
        var second = await store.GetOrCreateTraceAsync("second.asc", 2);

        var list = await store.ListTracesAsync(2);

        list.Select(t => t.TraceId).Should().Equal([second, first]);
    }
}