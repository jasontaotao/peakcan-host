using FluentAssertions;
using PeakCan.Host.Mobile.Core.Platform;
using PeakCan.Host.Mobile.Core.Services;
using Xunit;

namespace PeakCan.Host.Mobile.Core.Tests.Services;

public class TraceFileCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "peakcan-cache-test-" + Guid.NewGuid().ToString("N"));

    public TraceFileCacheTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static PickedTraceFile Pick(string name, byte[] content)
        => new(name, content.Length, _ => Task.FromResult<Stream>(new MemoryStream(content)));

    [Fact]
    public async Task ImportAsync_WritesFile_AndReturnsPath()
    {
        var cache = new TraceFileCache(_dir);
        var path = await cache.ImportAsync(Pick("foo.asc", new byte[] { 1, 2, 3 }));
        File.Exists(path).Should().BeTrue();
        (await File.ReadAllBytesAsync(path)).Should().Equal(new byte[] { 1, 2, 3 });
    }

    [Fact]
    public async Task ImportAsync_SameNameAndSize_ReusesWithoutCopying()
    {
        var cache = new TraceFileCache(_dir);
        bool opened = false;
        var pick = new PickedTraceFile(
            "foo.asc",
            3,
            _ => { opened = true; return Task.FromResult<Stream>(new MemoryStream(new byte[] { 1, 2, 3 })); });

        var p1 = await cache.ImportAsync(pick);
        opened.Should().BeTrue("first import must copy");

        opened = false;
        var p2 = await cache.ImportAsync(pick);
        p2.Should().Be(p1);
        opened.Should().BeFalse("second import should not re-open the stream (cache hit)");
    }

    [Fact]
    public void FindCached_ReturnsNull_WhenAbsent()
    {
        var cache = new TraceFileCache(_dir);
        cache.FindCached("missing.asc", 123).Should().BeNull();
    }
}

