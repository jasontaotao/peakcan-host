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
}