using System.IO;
using System.Reflection;
using FluentAssertions;

namespace PeakCan.Host.App.Tests.Views;

/// <summary>
/// v3.12.0 MINOR C2: anti-regression guard. Asserts the
/// <c>ReplayViewModel</c> responsibility regions live in distinct
/// <c>.partial.cs</c> files matching the file structure in
/// <c>docs/superpowers/plans/2026-07-07-v3-12-0-minor-*.md</c>.
/// Future contributors who merge everything back into
/// <c>ReplayViewModel.cs</c> break this test.
/// </summary>
public sealed class ReplayViewModelPartialSplitTests
{
    /// <summary>
    /// Walk from <see cref="Assembly.Location"/> up to the repo root,
    /// find <c>ReplayViewModel.cs</c>'s directory, then assert the four
    /// partial files exist. Mirror the path-walk in
    /// <c>TraceViewerViewXamlTests.cs</c> (v3.11.6 PATCH regression-guard).
    /// </summary>
    [Fact]
    public void ReplayViewModel_HasFour_PartialClassFiles_ForResponsibilitySplit()
    {
        // Arrange: find the ReplayViewModel.cs directory by walking up
        // from the test assembly's bin folder to the repo root.
        var asmDir = Path.GetDirectoryName(typeof(PeakCan.Host.App.ViewModels.ReplayViewModel).Assembly.Location)!;
        var dir = new DirectoryInfo(asmDir);
        DirectoryInfo? vmDir = null;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "PeakCan.Host.App", "ViewModels");
            if (Directory.Exists(candidate))
            {
                vmDir = new DirectoryInfo(candidate);
                break;
            }
            dir = dir.Parent;
        }
        vmDir.Should().NotBeNull("test assembly must be located inside the repo build output");

        // Act: assert each partial file is present.
        var expected = new[]
        {
            "ReplayViewModel.cs",                    // core (ctor + DI fields + dispose)
            "ReplayViewModel.Loader.partial.cs",     // OpenAsync + recent + OpenSessionAsync
            "ReplayViewModel.Playback.partial.cs",   // transport + filter
            "ReplayViewModel.Bookmarks.partial.cs",  // bookmarks + loop regions
            "ReplayViewModel.Bundle.partial.cs",     // BuildSnapshot* + Save/Open bundle
        };

        // Assert: each file exists.
        foreach (var name in expected)
        {
            File.Exists(Path.Combine(vmDir!.FullName, name))
                .Should().BeTrue($"v3.12.0 MINOR C2 requires ReplayViewModel split into '{name}'");
        }
    }

    /// <summary>
    /// Asserts the core <c>ReplayViewModel.cs</c> file is no longer
    /// god-class sized (was 1190 LoC pre-split; post-split target is
    /// ~280 LoC). Hard cap at 500 LoC leaves headroom for the inevitable
    /// future responsibility-region drift without re-triggering the
    /// original god-class problem.
    /// </summary>
    [Fact]
    public void ReplayViewModel_CoreFile_IsUnder500Lines_PostSplit()
    {
        var asmDir = Path.GetDirectoryName(typeof(PeakCan.Host.App.ViewModels.ReplayViewModel).Assembly.Location)!;
        var dir = new DirectoryInfo(asmDir);
        string? corePath = null;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "PeakCan.Host.App", "ViewModels", "ReplayViewModel.cs");
            if (File.Exists(candidate))
            {
                corePath = candidate;
                break;
            }
            dir = dir.Parent;
        }
        corePath.Should().NotBeNull("core file must exist somewhere in the repo");

        var lineCount = File.ReadAllLines(corePath!).Length;
        lineCount.Should().BeLessThanOrEqualTo(500,
            $"v3.12.0 MINOR C2 split moves logic out of ReplayViewModel.cs into .partial.cs files; " +
            $"the core file must stay under 500 LoC to keep the god-class regression from re-emerging");
    }

    /// <summary>
    /// Asserts the Loader partial contains the file-open command,
    /// the bundle-open method, and the recent-sessions projection.
    /// If a future contributor merges these back into the core file
    /// (or moves them to a different region), this test fires.
    /// </summary>
    [Fact]
    public void LoaderPartial_ContainsExpectedPublicSurface()
    {
        var asmDir = Path.GetDirectoryName(typeof(PeakCan.Host.App.ViewModels.ReplayViewModel).Assembly.Location)!;
        var dir = new DirectoryInfo(asmDir);
        string? path = null;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "PeakCan.Host.App", "ViewModels", "ReplayViewModel.Loader.partial.cs");
            if (File.Exists(candidate)) { path = candidate; break; }
            dir = dir.Parent;
        }
        path.Should().NotBeNull("Loader partial must exist after Tasks 2-5");

        var content = File.ReadAllText(path!);
        content.Should().Contain("OpenAsync", "Loader owns the file-open command");
        content.Should().Contain("OpenSessionAsync", "Loader owns the bundle-open method");
        content.Should().Contain("RefreshRecentEntries", "Loader owns the recent-sessions refresh");
        content.Should().Contain("RecentSessionEntries", "Loader owns the recent-sessions VM projection");
    }

    /// <summary>
    /// Asserts the Playback partial contains the transport commands,
    /// the filter callbacks, and the frame-step binary searches.
    /// </summary>
    [Fact]
    public void PlaybackPartial_ContainsExpectedPublicSurface()
    {
        var asmDir = Path.GetDirectoryName(typeof(PeakCan.Host.App.ViewModels.ReplayViewModel).Assembly.Location)!;
        var dir = new DirectoryInfo(asmDir);
        string? path = null;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "PeakCan.Host.App", "ViewModels", "ReplayViewModel.Playback.partial.cs");
            if (File.Exists(candidate)) { path = candidate; break; }
            dir = dir.Parent;
        }
        path.Should().NotBeNull("Playback partial must exist after Tasks 2-5");

        var content = File.ReadAllText(path!);
        content.Should().Contain("NextFrame", "Playback owns the frame-step-forward command");
        content.Should().Contain("PrevFrame", "Playback owns the frame-step-back command");
        content.Should().Contain("SetSpeed", "Playback owns the speed-multiplier command");
        content.Should().Contain("SeekTo", "Playback owns the absolute-timestamp seek command");
        content.Should().Contain("OnCanIdFilterTextChanged", "Playback owns the CAN-ID filter partial callback");
    }

    /// <summary>
    /// Asserts the Bookmarks partial contains the bookmark + loop-region
    /// commands and the public collections backing them.
    /// </summary>
    [Fact]
    public void BookmarksPartial_ContainsExpectedPublicSurface()
    {
        var asmDir = Path.GetDirectoryName(typeof(PeakCan.Host.App.ViewModels.ReplayViewModel).Assembly.Location)!;
        var dir = new DirectoryInfo(asmDir);
        string? path = null;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "PeakCan.Host.App", "ViewModels", "ReplayViewModel.Bookmarks.partial.cs");
            if (File.Exists(candidate)) { path = candidate; break; }
            dir = dir.Parent;
        }
        path.Should().NotBeNull("Bookmarks partial must exist after Tasks 2-5");

        var content = File.ReadAllText(path!);
        content.Should().Contain("AddBookmark", "Bookmarks owns the bookmark-capture command");
        content.Should().Contain("AddLoopRegion", "Bookmarks owns the loop-region-capture command");
        content.Should().Contain("ClearLoopRegions", "Bookmarks owns the loop-region-clear command");
        content.Should().Contain("public ObservableCollection<BookmarkVm>", "Bookmarks owns the public bookmarks collection");
        content.Should().Contain("public ObservableCollection<LoopRegionVm>", "Bookmarks owns the public loop-regions collection");
    }

    /// <summary>
    /// Asserts the Bundle partial contains the snapshot builder + the
    /// save/open bundle commands + the logger-message helpers.
    /// </summary>
    [Fact]
    public void BundlePartial_ContainsExpectedPublicSurface()
    {
        var asmDir = Path.GetDirectoryName(typeof(PeakCan.Host.App.ViewModels.ReplayViewModel).Assembly.Location)!;
        var dir = new DirectoryInfo(asmDir);
        string? path = null;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "PeakCan.Host.App", "ViewModels", "ReplayViewModel.Bundle.partial.cs");
            if (File.Exists(candidate)) { path = candidate; break; }
            dir = dir.Parent;
        }
        path.Should().NotBeNull("Bundle partial must exist after Tasks 2-5");

        var content = File.ReadAllText(path!);
        content.Should().Contain("BuildSnapshot", "Bundle owns the sync snapshot builder");
        content.Should().Contain("BuildSnapshotAsync", "Bundle owns the async snapshot builder");
        content.Should().Contain("SaveAsync", "Bundle owns the save-bundle command");
        content.Should().Contain("LogSourceMissing", "Bundle owns the source-missing log helper");
    }
}