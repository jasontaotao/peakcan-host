using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.Trace;

/// <summary>
/// v3.2.0 MINOR: pins the registry's lifecycle + deep-copy + event contracts.
/// All tests use an in-memory fake palette (deterministic colors per sourceId).
/// </summary>
public class TraceSessionRegistryTests
{
    [Fact]
    public async Task LoadAsync_AddsSource_WithUniqueIdAndPaletteColor()
    {
        var palette = new TableauPalette();
        var registry = new TraceSessionRegistry(palette, NullLoggerFactory.Instance);

        var source = await registry.LoadAsync(WriteTwoFrameAsc(out var path));

        source.SourceId.Should().NotBeNullOrEmpty();
        source.DisplayName.Should().Be("trace", "GetFileNameWithoutExtension strips the extension");
        source.Path.Should().Be(path);
        source.Color.Should().Be(palette.PickColorFor(source.SourceId));
        registry.Sources.Should().ContainSingle().Which.Should().Be(source);
    }

    [Fact]
    public async Task LoadAsync_SamePath_AddsSecondSource_WithDistinctId()
    {
        var palette = new TableauPalette();
        var registry = new TraceSessionRegistry(palette, NullLoggerFactory.Instance);
        var path = WriteTwoFrameAsc(out _);

        var first = await registry.LoadAsync(path);
        var second = await registry.LoadAsync(path);

        first.SourceId.Should().NotBe(second.SourceId);
        registry.Sources.Should().HaveCount(2);
    }

    [Fact]
    public async Task UnloadAsync_RemovesSource_AndDisposesService()
    {
        var palette = new TableauPalette();
        var registry = new TraceSessionRegistry(palette, NullLoggerFactory.Instance);
        var source = await registry.LoadAsync(WriteTwoFrameAsc(out _));

        await registry.UnloadAsync(source.SourceId);

        registry.Sources.Should().BeEmpty();
    }

    [Fact]
    public async Task GetFrames_ReturnsDefensiveCopy_NotInternalList()
    {
        // v3.2.0 MINOR: ITraceViewerService.LoadedFrames exposes internal
        // storage directly (no defensive copy); the registry MUST copy at
        // the boundary so concurrent consumers cannot observe each other's
        // mutations through the registry's view.
        var palette = new TableauPalette();
        var registry = new TraceSessionRegistry(palette, NullLoggerFactory.Instance);
        var source = await registry.LoadAsync(WriteTwoFrameAsc(out _));

        var viewA = registry.GetFrames(source.SourceId);
        var viewB = registry.GetFrames(source.SourceId);

        viewA.Should().NotBeSameAs(viewB,
            "the registry must defensively copy at the boundary — each GetFrames call returns a fresh array");
        viewA.Should().BeEquivalentTo(viewB, "the contents should match");
    }

    [Fact]
    public async Task SourcesChanged_Fires_OnLoadAndUnload()
    {
        var palette = new TableauPalette();
        var registry = new TraceSessionRegistry(palette, NullLoggerFactory.Instance);
        var fired = 0;
        registry.SourcesChanged += () => fired++;

        var source = await registry.LoadAsync(WriteTwoFrameAsc(out _));
        await registry.UnloadAsync(source.SourceId);

        fired.Should().Be(2, "one fire on Load, one on Unload");
    }

    [Fact]
    public async Task LoadAsync_PastCapacity10_ThrowsInvalidOperationException()
    {
        var palette = new TableauPalette();
        var registry = new TraceSessionRegistry(palette, NullLoggerFactory.Instance);

        var act = async () =>
        {
            for (var i = 0; i < 11; i++)
                await registry.LoadAsync(WriteTwoFrameAsc(out _));
        };

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*10*");
    }

    [Fact]
    public async Task LoadAsync_NonexistentPath_DoesNotBurnPaletteSlot()
    {
        // v3.2.0 MINOR HIGH review fix: palette assignment must happen AFTER
        // the ASC parse succeeds. If the file does not exist, the parse
        // throws ReplayLoadException and the palette slot for the
        // never-registered sourceId must NOT be reserved.
        var palette = new TableauPalette();
        var registry = new TraceSessionRegistry(palette, NullLoggerFactory.Instance);

        var act = async () => await registry.LoadAsync(@"C:/definitely/does/not/exist.asc");

        await act.Should().ThrowAsync<ReplayLoadException>();

        // Palette should still be empty — no slot consumed by the failed load.
        var act2 = async () =>
        {
            for (var i = 0; i < 10; i++)
                await registry.LoadAsync(WriteTwoFrameAsc(out _));
        };
        await act2.Should().NotThrowAsync("the failed LoadAsync must not have burned any palette slot");
    }

    // --- Helpers ----------------------------------------------------

    /// <summary>
    /// Writes a tiny ASC file to %TEMP% and returns its path.
    /// Two frames on the same channel+id (matches the AscParser test fixture style).
    /// </summary>
    private static string WriteTwoFrameAsc(out string path)
    {
        var dir = Path.Combine(Path.GetTempPath(), "peakcan-host-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        path = Path.Combine(dir, "trace.asc");
        File.WriteAllText(path,
            "   0.000000 51  100  8  11 22 33 44 55 66 77 88\n" +
            "   1.000000 51  100  8  AA BB CC DD EE FF 00 11\n");
        return path;
    }
}