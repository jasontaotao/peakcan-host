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
}
