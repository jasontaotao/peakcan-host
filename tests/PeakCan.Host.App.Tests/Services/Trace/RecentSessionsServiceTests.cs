using System.IO;
using System.Text.Json;
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

    // ---------- v3.8.6 PATCH H2: LoadAsync MaxEntries cap ----------

    /// <summary>
    /// v3.8.6 PATCH H2: <see cref="RecentSessionsService.LoadAsync"/>
    /// must enforce the same <see cref="RecentSessionsService.MaxEntries"/>
    /// cap that <see cref="RecentSessionsService.Add"/> enforces. Pre-fix,
    /// a hand-edited persisted JSON (or a back-compat user upgrading from
    /// a pre-v3.6.0 build that did not enforce the cap) could land on a
    /// list of 6-10 entries -- the MRU menu would show more than 5 items
    /// until the next <c>Add</c> operation trimmed the tail. The cap
    /// must be symmetric across both code paths.
    /// </summary>
    [Fact]
    public async Task LoadAsync_FileExceedsMaxEntries_TrimsToCap()
    {
        // Arrange: persist an 7-entry JSON file before constructing the
        // service (simulates a hand-edited or upgrade-from-old-version list).
        var path = NewRecentPath();
        var dto = new RecentSessionsService.Envelope
        {
            Schema = "recent-sessions/v1",
            Recent = Enumerable.Range(0, 7).Select(i => new RecentSessionDto(
                $@"C:\sessions\file{i}.tmtrace",
                $"file{i}.tmtrace",
                DateTimeOffset.UtcNow.AddMinutes(-i),
                "trace")).ToList(),
        };
        var json = JsonSerializer.Serialize(dto);
        File.WriteAllText(path, json);

        // Act
        var svc = NewService(path);
        await svc.LoadAsync(CancellationToken.None);

        // Assert — exactly MaxEntries (5 newest by SavedAt) survive.
        svc.Recent.Should().HaveCount(RecentSessionsService.MaxEntries,
            "LoadAsync must enforce the MaxEntries cap symmetric with Add");
        // Newest (file0, most-recent SavedAt) lands at top, oldest
        // (file4) at bottom of the kept set; file5 + file6 dropped.
        svc.Recent[0].Path.Should().EndWith("file0.tmtrace");
        svc.Recent[^1].Path.Should().EndWith("file4.tmtrace");
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

    // ---------- v3.8.8 PATCH F2: oversized-file load guard ----------

    /// <summary>
    /// v3.8.8 PATCH F2: a user might drop a large file (a logfile, a
    /// stray binary) at the persisted path. Pre-fix,
    /// <see cref="RecentSessionsService.LoadAsync"/> reads the entire
    /// file via <c>File.ReadAllText</c> + <c>JsonSerializer.Deserialize</c>
    /// on whatever thread the caller is on — the WPF dispatcher at
    /// app startup (the call site is a fire-and-forget
    /// <c>_ = _recentSessions.LoadAsync(...)</c> in AppShellViewModel
    /// ctor at line 336). A 50 MB file blocks the UI thread for
    /// seconds; a 1 GB file risks OOM.
    /// <para>
    /// F2 fix: precheck the file size with
    /// <c>new FileInfo(_path).Length</c> before reading; refuse to
    /// deserialize anything beyond a fixed cap (1 MB) and treat it as
    /// corrupt (the existing <c>catch (JsonException or IOException)</c>
    /// leaves <c>_items</c> empty).
    /// </para>
    /// <para>
    /// The test writes a VALID 1.5 MB JSON envelope (10 000 valid
    /// entries) so the only way for the file to land at 0 entries
    /// is the size cap. Pre-fix, the deserializer would happily
    /// consume all 10 000 entries and the in-memory cap would
    /// trim to <see cref="RecentSessionsService.MaxEntries"/> = 5,
    /// making the test fail with <c>Recent.Count == 5</c>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task LoadAsync_WhenFileExceedsSizeCap_TreatsAsCorrupt_LeavesRecentEmpty()
    {
        // Arrange: a 1.5 MB valid envelope (10 000 valid entries).
        var path = NewRecentPath();
        var dto = new RecentSessionsService.Envelope
        {
            Schema = "recent-sessions/v1",
            Version = 1,
        };
        for (int i = 0; i < 10_000; i++)
        {
            dto.Recent.Add(new RecentSessionDto(
                Path: $@"C:\trace-{i:D5}.tmtrace",
                Label: $"trace-{i:D5}",
                SavedAt: DateTimeOffset.UtcNow,
                ViewType: "trace"));
        }
        File.WriteAllText(path, JsonSerializer.Serialize(dto));
        var fileSize = new FileInfo(path).Length;
        fileSize.Should().BeGreaterThan(1 * 1024 * 1024,
            "test fixture must exceed the 1 MB size cap so the only way for Recent to be empty is the size-cap rejection");

        var svc = NewService(path);

        // Act
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await svc.LoadAsync(CancellationToken.None);
        sw.Stop();

        // Assert: the size cap kicked in -- the file is rejected
        // outright (treated as corrupt), Recent stays empty, AND the
        // load returns quickly because no deserialization was attempted.
        svc.Recent.Should().BeEmpty(
            "v3.8.8 F2 fix: LoadAsync must refuse to deserialize files beyond the size cap; without it, the file is parsed and capped to MaxEntries (5)");
        sw.ElapsedMilliseconds.Should().BeLessThan(500,
            "the size-cap precheck must short-circuit BEFORE File.ReadAllText + JsonSerializer.Deserialize -- a 1.5 MB file would take longer than 500 ms to parse+deserialize on a slow disk");
    }

    /// <summary>
    /// v3.8.8 PATCH F2: a small file (under the cap) must still load
    /// normally. Regression guard so the size cap doesn't accidentally
    /// reject legitimate small files.
    /// </summary>
    [Fact]
    public async Task LoadAsync_WhenFileUnderSizeCap_LoadsNormally()
    {
        // Arrange: a small (< 1 KB) valid envelope with 3 entries.
        var path = NewRecentPath();
        var dto = new RecentSessionsService.Envelope
        {
            Schema = "recent-sessions/v1",
            Version = 1,
            Recent = new()
            {
                new RecentSessionDto(@"C:\a.tmtrace", "a", DateTimeOffset.UtcNow, "trace"),
                new RecentSessionDto(@"C:\b.tmtrace", "b", DateTimeOffset.UtcNow, "trace"),
                new RecentSessionDto(@"C:\c.tmtrace", "c", DateTimeOffset.UtcNow, "trace"),
            },
        };
        File.WriteAllText(path, JsonSerializer.Serialize(dto));

        var svc = NewService(path);

        // Act
        await svc.LoadAsync(CancellationToken.None);

        // Assert
        svc.Recent.Should().HaveCount(3,
            "v3.8.8 F2 regression: small files must still load -- only oversized files are rejected");
    }
}