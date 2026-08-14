using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services.Ui;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.Ui;

/// <summary>
/// P0-1: pins the six core behaviors of <see cref="WindowStateStore"/>:
/// <list type="number">
/// <item>Set + LoadAsync across instances round-trips each window's state;</item>
/// <item>Get with no persisted entry returns null;</item>
/// <item>LoadAsync with a missing file leaves the store empty;</item>
/// <item>corrupt JSON is treated as empty (never throws);</item>
/// <item>oversized file is rejected (mirror RecentSessionsService 1 MB cap);</item>
/// <item>multiple keys persist independently and Set overwrites in place.</item>
/// </list>
/// Each test uses a per-test temp directory so parallel xunit execution is
/// safe and no fixture leaks into a sibling test.
/// </summary>
public sealed class WindowStateStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _files = new();

    public WindowStateStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"winstate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in _files)
                if (File.Exists(f)) File.Delete(f);
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* best effort */ }
    }

    private string NewPath() => Track(Path.Combine(_tempDir, $"winstate-{Guid.NewGuid():N}.json"));

    private string Track(string p) { _files.Add(p); return p; }

    private static WindowStateStore NewStore(string path) =>
        new(NullLogger<WindowStateStore>.Instance, path);

    [Fact]
    public async Task Set_ThenLoadAcrossInstances_RoundTrips()
    {
        // Arrange
        var path = NewPath();
        var first = NewStore(path);
        first.Set(WindowKey.TraceViewer, new WindowStateDto(10, 20, 1200, 800, "Maximized"));

        // Act — fresh instance, LoadAsync, observe the same state.
        var second = NewStore(path);
        await second.LoadAsync(CancellationToken.None);

        // Assert
        var state = second.Get(WindowKey.TraceViewer);
        state.Should().NotBeNull();
        state!.Left.Should().Be(10);
        state.Top.Should().Be(20);
        state.Width.Should().Be(1200);
        state.Height.Should().Be(800);
        state.State.Should().Be("Maximized");
    }

    [Fact]
    public void Get_WhenNoEntry_ReturnsNull()
    {
        // Arrange / Act
        var store = NewStore(NewPath());

        // Assert
        store.Get(WindowKey.Uds).Should().BeNull();
    }

    [Fact]
    public async Task Load_WhenFileMissing_LeavesStoreEmpty()
    {
        // Arrange / Act
        var store = NewStore(NewPath());
        await store.LoadAsync(CancellationToken.None);

        // Assert
        store.Get(WindowKey.AppShell).Should().BeNull();
    }

    [Fact]
    public async Task Load_WhenFileCorrupt_TreatsAsEmpty_DoesNotThrow()
    {
        // Arrange
        var path = NewPath();
        File.WriteAllText(path, "{ this is not valid json ]");

        // Act
        var store = NewStore(path);
        await store.LoadAsync(CancellationToken.None);

        // Assert
        store.Get(WindowKey.AppShell).Should().BeNull();
    }

    [Fact]
    public async Task Load_WhenFileExceedsSizeCap_TreatsAsEmpty()
    {
        // Arrange: > 1 MB of garbage at the persisted path.
        var path = NewPath();
        File.WriteAllText(path, new string('x', 2 * 1024 * 1024));

        // Act
        var store = NewStore(path);
        await store.LoadAsync(CancellationToken.None);

        // Assert
        store.Get(WindowKey.AppShell).Should().BeNull();
    }

    [Fact]
    public void Set_MultipleKeysPersistIndependently_AndSetOverwritesInPlace()
    {
        // Arrange
        var store = NewStore(NewPath());
        store.Set(WindowKey.Uds, new WindowStateDto(1, 2, 1100, 700, "Normal"));
        store.Set(WindowKey.Hil, new WindowStateDto(3, 4, 900, 600, "Normal"));

        // Act — overwrite Uds, leave Hil untouched.
        store.Set(WindowKey.Uds, new WindowStateDto(5, 6, 1150, 720, "Maximized"));

        // Assert
        store.Get(WindowKey.Uds).Should().BeEquivalentTo(
            new WindowStateDto(5, 6, 1150, 720, "Maximized"));
        store.Get(WindowKey.Hil).Should().BeEquivalentTo(
            new WindowStateDto(3, 4, 900, 600, "Normal"));
    }

    [Fact]
    public async Task Set_PersistsToDisk_Immediately()
    {
        // Arrange
        var path = NewPath();
        var store = NewStore(path);

        // Act
        store.Set(WindowKey.AppShell, new WindowStateDto(0, 0, 1280, 720, "Normal"));

        // Assert — file exists with the entry (atomic tmp+rename path
        // exercised); a fresh instance that loads sees it without an
        // explicit save call.
        File.Exists(path).Should().BeTrue();
        var reloaded = NewStore(path);
        await reloaded.LoadAsync(CancellationToken.None);
        var state = reloaded.Get(WindowKey.AppShell);
        state.Should().NotBeNull();
        state!.Width.Should().Be(1280);
    }
}
