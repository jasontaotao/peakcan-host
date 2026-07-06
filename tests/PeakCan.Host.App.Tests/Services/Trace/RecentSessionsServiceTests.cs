using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.App.Services.Trace;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.Trace;

/// <summary>
/// v3.6.0 MINOR T3: pins the six core behaviors of
/// <see cref="RecentSessionsService"/>:
/// <list type="number">
/// <item>first <c>Add</c> populates the empty list;</item>
/// <item>duplicate <c>Add</c> moves the entry to top without doubling;</item>
/// <item>cap at <see cref="RecentSessionsService.MaxEntries"/> when more are added;</item>
/// <item><c>Remove</c> drops the matching path while preserving the rest;</item>
/// <item><c>Clear</c> empties both the in-memory list and the backing file;</item>
/// <item>the file persists across instances (round-trip).</item>
/// </list>
/// Each test uses a per-test temp directory under
/// <see cref="Path.GetTempPath"/> so parallel xunit execution is safe
/// and the fixture cannot leak state into a sibling test.
/// </summary>
public sealed class RecentSessionsServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _files = new();

    public RecentSessionsServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"recent-{Guid.NewGuid():N}");
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

    private string NewRecentPath() =>
        Track(Path.Combine(_tempDir, $"recent-{Guid.NewGuid():N}.json"));

    private string Track(string p) { _files.Add(p); return p; }

    private static RecentSessionsService NewService(string path) =>
        new(NullLogger<RecentSessionsService>.Instance, path);

    [Fact]
    public void Add_AppendsPathToTop_OfEmpty()
    {
        // Arrange
        var path = NewRecentPath();
        var svc = NewService(path);

        // Act
        svc.Add(@"C:\sessions\highway.tmtrace");

        // Assert
        svc.Recent.Should().HaveCount(1);
        var entry = svc.Recent[0];
        entry.Path.Should().Be(@"C:\sessions\highway.tmtrace");
        entry.Label.Should().Be("highway.tmtrace");
    }

    [Fact]
    public void Add_MovesExistingToTop_DoesNotDuplicate()
    {
        // Arrange
        var path = NewRecentPath();
        var svc = NewService(path);
        svc.Add(@"C:\a\b1.tmtrace");
        svc.Add(@"C:\a\b2.tmtrace");
        svc.Add(@"C:\a\b3.tmtrace");
        // Order is now: [b3, b2, b1]

        // Act — re-Add b1, expect it moves to top and is NOT duplicated.
        svc.Add(@"C:\a\b1.tmtrace");

        // Assert
        svc.Recent.Should().HaveCount(3);
        svc.Recent[0].Path.Should().Be(@"C:\a\b1.tmtrace");
        svc.Recent[1].Path.Should().Be(@"C:\a\b3.tmtrace");
        svc.Recent[2].Path.Should().Be(@"C:\a\b2.tmtrace");
    }

    [Fact]
    public void Add_CapsAt5_WhenMoreThanMaxAdded()
    {
        // Arrange
        var path = NewRecentPath();
        var svc = NewService(path);

        // Act — push 7 distinct paths.
        for (var i = 0; i < 7; i++)
        {
            svc.Add($@"C:\sessions\s{i}.tmtrace");
        }

        // Assert — only the 5 most-recent remain; oldest two dropped.
        svc.Recent.Should().HaveCount(RecentSessionsService.MaxEntries);
        // Most-recent first means s6 at top, s2 at bottom of the kept set.
        svc.Recent[0].Path.Should().EndWith("s6.tmtrace");
        svc.Recent[4].Path.Should().EndWith("s2.tmtrace");
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public void Remove_DropsPath_AndPreservesOrderOfRest()
    {
        // Arrange
        var path = NewRecentPath();
        var svc = NewService(path);
        svc.Add(@"C:\a\b1.tmtrace");
        svc.Add(@"C:\a\b2.tmtrace");
        svc.Add(@"C:\a\b3.tmtrace");
        // Order: [b3, b2, b1]

        // Act — case-insensitive match: lowercase the middle path and
        // confirm the service treats it as the same entry.
        svc.Remove(@"c:\a\B2.tmtrace");

        // Assert
        svc.Recent.Should().HaveCount(2);
        svc.Recent[0].Path.Should().Be(@"C:\a\b3.tmtrace");
        svc.Recent[1].Path.Should().Be(@"C:\a\b1.tmtrace");
    }

    [Fact]
    public void Clear_EmptiesList_AndEmptiesFile()
    {
        // Arrange
        var path = NewRecentPath();
        var svc = NewService(path);
        svc.Add(@"C:\a\b1.tmtrace");
        svc.Add(@"C:\a\b2.tmtrace");
        File.Exists(path).Should().BeTrue();

        // Act
        svc.Clear();

        // Assert
        svc.Recent.Should().BeEmpty();
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public async Task PersistAcrossInstances_LoadReturnsWhatAddWrote()
    {
        // Arrange
        var path = NewRecentPath();
        var first = NewService(path);
        first.Add(@"C:\sessions\highway.tmtrace");
        first.Add(@"C:\sessions\drive_downtown.tmtrace");

        // Act — fresh instance, LoadAsync, observe the same entries.
        var second = NewService(path);
        await second.LoadAsync(CancellationToken.None);

        // Assert
        second.Recent.Should().HaveCount(2);
        second.Recent[0].Path.Should().Be(@"C:\sessions\drive_downtown.tmtrace");
        second.Recent[1].Path.Should().Be(@"C:\sessions\highway.tmtrace");
        second.Recent[0].Label.Should().Be("drive_downtown.tmtrace");
        second.Recent[1].Label.Should().Be("highway.tmtrace");
    }

    // ---------- v3.7.0 MINOR Chunk 2: viewType discriminator ----------

    /// <summary>
    /// v3.7.0 MINOR Chunk 2: the explicit-viewType overload stores the
    /// viewType string on the entry. The default no-viewType overload
    /// falls back to <c>"trace"</c> for source compatibility with the
    /// v3.6.x callers.
    /// </summary>
    [Fact]
    public void Add_WithViewType_StoresViewType()
    {
        // Arrange
        var path = NewRecentPath();
        var svc = NewService(path);

        // Act
        svc.Add(@"C:\sessions\replay.tmtrace", viewType: "replay");

        // Assert
        svc.Recent.Should().HaveCount(1);
        svc.Recent[0].Path.Should().Be(@"C:\sessions\replay.tmtrace");
        svc.Recent[0].ViewType.Should().Be("replay");

        // The default overload (no viewType) is the v3.6.x back-compat
        // entry point; it must default to "trace" so the AppShell menu
        // still finds those legacy callers.
        var legacyPath = NewRecentPath();
        var legacySvc = NewService(legacyPath);
        legacySvc.Add(@"C:\sessions\legacy.tmtrace");
        legacySvc.Recent[0].ViewType.Should().Be("trace");
    }

    /// <summary>
    /// v3.7.0 MINOR Chunk 2: <see cref="RecentSessionsService.Clear(string)"/>
    /// removes only entries whose <see cref="RecentSessionDto.ViewType"/>
    /// matches the argument. The on-disk JSON file is left alone when
    /// other viewType entries remain; deleted when the list becomes
    /// empty as a result.
    /// </summary>
    [Fact]
    public void Clear_WithViewType_RemovesOnlyMatchingEntries()
    {
        // Arrange
        var path = NewRecentPath();
        var svc = NewService(path);
        svc.Add(@"C:\x\trace.tmtrace", viewType: "trace");
        svc.Add(@"C:\y\replay1.tmtrace", viewType: "replay");
        svc.Add(@"C:\z\replay2.tmtrace", viewType: "replay");
        svc.Recent.Should().HaveCount(3);

        // Act
        svc.Clear("replay");

        // Assert
        svc.Recent.Should().HaveCount(1);
        svc.Recent[0].Path.Should().Be(@"C:\x\trace.tmtrace");
        svc.Recent[0].ViewType.Should().Be("trace");
    }
}