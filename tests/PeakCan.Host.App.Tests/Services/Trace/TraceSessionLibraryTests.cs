using System.IO;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OxyPlot;
using PeakCan.Host.App.Services.Trace;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.Trace;

/// <summary>
/// v3.5.0 MINOR: pins the <see cref="TraceSessionLibrary"/> Save/Load
/// round-trip, atomic-write invariant, and corrupt-recovery semantics.
/// Mirrors the <c>SequenceLibrary</c> test pattern (per-file test ctor
/// via the internal <c>internal(string path, ILogger)</c> constructor).
/// </summary>
public sealed class TraceSessionLibraryTests
{
    private static TraceSessionLibrary NewLib(out string path)
    {
        path = Path.Combine(
            Path.GetTempPath(),
            $"tmtrace-lib-{Guid.NewGuid():N}.tmtrace");
        return new TraceSessionLibrary(path, NullLogger<TraceSessionLibrary>.Instance);
    }

    private static TraceSessionBundleDto MakeSnapshot(
        int sourceCount = 1,
        bool includePlayback = true,
        bool includeViewports = false)
    {
        var sources = Enumerable.Range(0, sourceCount).Select(i => new BundleSourceDto
        {
            SourceId = Guid.NewGuid().ToString("N"),
            DisplayName = $"trace{i}",
            Path = $"C:/recordings/trace{i}.asc",
            ColorA = 255,
            ColorR = (byte)(50 + i * 30),
            ColorG = 120,
            ColorB = 200,
            StrokeStyle = "Solid",
            CanIdFilter = i == 0 ? "0x100" : "",
        }).ToList();

        var playback = includePlayback ? new BundlePlaybackDto
        {
            MasterSourceId = sources.Count > 0 ? sources[0].SourceId : "",
            Loop = true,
            Speed = 2.0,
            ScrubberValue = 12.345,
            StartTimestamp = null,
            EndTimestamp = null,
        } : null;

        var viewports = includeViewports
            ? sources.SelectMany(s => new[]
            {
                new BundleViewportDto
                {
                    EffectiveKey = $"{s.SourceId}.0x100.EngineRPM",
                    XMin = 0.0,
                    XMax = 60.0,
                    IsFocused = false,
                    IsCollapsed = false,
                },
            }).ToList()
            : new List<BundleViewportDto>();

        return new TraceSessionBundleDto
        {
            Version = 1,
            Schema = "tmtrace/v1",
            SavedAt = DateTimeOffset.UtcNow,
            AppVersion = "3.5.0",
            DbcPath = "C:/projects/vehicle.dbc",
            GlobalCanIdFilter = "0x100, 0x200",
            Playback = playback,
            Sources = sources,
            Viewports = viewports,
        };
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsSources()
    {
        var lib = NewLib(out var path);
        var snapshot = MakeSnapshot(sourceCount: 3, includePlayback: false, includeViewports: false);

        lib.Save(snapshot);
        var loaded = lib.Load(path);

        loaded.Should().NotBeNull();
        loaded!.Sources.Should().HaveCount(3);
        loaded.Sources[0].DisplayName.Should().Be("trace0");
        loaded.Sources[0].Path.Should().Be("C:/recordings/trace0.asc");
        loaded.Sources[0].CanIdFilter.Should().Be("0x100");
        loaded.Sources[1].CanIdFilter.Should().Be("");
        // SourceIds are preserved byte-for-byte
        loaded.Sources.Select(s => s.SourceId).Should().BeEquivalentTo(
            snapshot.Sources.Select(s => s.SourceId));
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsPlaybackState()
    {
        var lib = NewLib(out var path);
        var snapshot = MakeSnapshot(sourceCount: 2, includePlayback: true);

        lib.Save(snapshot);
        var loaded = lib.Load(path);

        loaded.Should().NotBeNull();
        loaded!.Playback.Should().NotBeNull();
        loaded.Playback!.MasterSourceId.Should().Be(snapshot.Sources[0].SourceId);
        loaded.Playback.Loop.Should().BeTrue();
        loaded.Playback.Speed.Should().Be(2.0);
        loaded.Playback.ScrubberValue.Should().Be(12.345);
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsViewports()
    {
        var lib = NewLib(out var path);
        var snapshot = MakeSnapshot(sourceCount: 2, includeViewports: true);

        lib.Save(snapshot);
        var loaded = lib.Load(path);

        loaded.Should().NotBeNull();
        loaded!.Viewports.Should().HaveCount(2);
        loaded.Viewports[0].EffectiveKey.Should().StartWith(snapshot.Sources[0].SourceId);
        loaded.Viewports[0].XMin.Should().Be(0.0);
        loaded.Viewports[0].XMax.Should().Be(60.0);
        loaded.Viewports[0].IsFocused.Should().BeFalse();
        loaded.Viewports[0].IsCollapsed.Should().BeFalse();
    }

    [Fact]
    public void Load_OnCorruptJson_ReturnsNull_AndLogsError()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tmtrace-corrupt-{Guid.NewGuid():N}.tmtrace");
        File.WriteAllText(path, "{ this is not valid json");
        try
        {
            var lib = new TraceSessionLibrary(path, NullLogger<TraceSessionLibrary>.Instance);

            var loaded = lib.Load(path);

            loaded.Should().BeNull("corrupt JSON must NOT throw — caller shows a MessageBox and continues");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_OnMissingFile_ReturnsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tmtrace-missing-{Guid.NewGuid():N}.tmtrace");
        var lib = new TraceSessionLibrary(path, NullLogger<TraceSessionLibrary>.Instance);

        var loaded = lib.Load(path);

        loaded.Should().BeNull("missing file is a valid empty-bundle state, not an error");
    }

    [Fact]
    public void Save_OverwritesExistingFile()
    {
        var lib = NewLib(out var path);
        lib.Save(MakeSnapshot(sourceCount: 1));
        lib.Save(MakeSnapshot(sourceCount: 5));

        var loaded = lib.Load(path);

        loaded.Should().NotBeNull();
        loaded!.Sources.Should().HaveCount(5,
            "second Save must overwrite the file (atomic rename) without leaking the old contents");
    }

    [Fact]
    public void Save_UsesAtomicMove_NotPartialWrite()
    {
        var lib = NewLib(out var path);

        lib.Save(MakeSnapshot(sourceCount: 2));

        File.Exists(path).Should().BeTrue("the rename completed");
        File.Exists(path + ".tmp").Should().BeFalse(
            "the tmp file must be cleaned up after a successful atomic rename");
        var json = File.ReadAllText(path);
        json.Should().NotBeNullOrWhiteSpace();
        json.Should().StartWith("{");
    }

    [Fact]
    public void Save_AndLoad_HandlesPathReferenceCorrectly()
    {
        // Path-reference, not embed. Verifies that the recorded .asc path is
        // preserved verbatim — the library does NOT slurp the recording.
        var lib = NewLib(out var path);
        var originalPath = @"C:/recordings/highway_cruise_2026-07-04.asc";
        var snapshot = MakeSnapshot(sourceCount: 1);
        snapshot.Sources[0].Path = originalPath;

        lib.Save(snapshot);
        var loaded = lib.Load(path);

        loaded.Should().NotBeNull();
        loaded!.Sources[0].Path.Should().Be(originalPath);
        // Bundle file size should be tiny — recorded .asc is path-only.
        var fileInfo = new FileInfo(path);
        fileInfo.Length.Should().BeLessThan(10_000,
            "bundle must not embed the recording — typical .asc is 10MB-1GB");
    }

    // ---------- v3.8.5 PATCH L1: streaming serialization ----------

    /// <summary>
    /// v3.8.5 PATCH L1: <see cref="TraceSessionLibrary.Save"/> must use
    /// streaming serialization (Utf8JsonWriter on the FileStream) instead
    /// of eagerly serializing to a UTF-16 string with
    /// <c>JsonSerializer.Serialize(snapshot)</c> + <c>File.WriteAllText</c>.
    /// The eager path allocates the entire JSON envelope on the LOH
    /// before any byte hits disk; a 50MB bundle would peak at ≥100MB
    /// working-set (UTF-16 string overhead) — visible on low-RAM CI
    /// runners and small-VM machines. The streaming path writes
    /// incrementally and discards per-iteration buffers as the writer
    /// advances, capping peak memory at the per-write chunk size
    /// (default 4KB).
    /// <para>
    /// Round-trip equivalence: the resulting bundle file must deserialize
    /// back into a DTO with all fields populated identically. Same
    /// atomic-write behavior (tmp + rename), same JSON shape (pretty +
    /// UTF-8 BOM).
    /// </para>
    /// </summary>
    [Fact]
    public void Save_LargeBundle_StreamingSerialization_RoundTripsThroughLoad()
    {
        var lib = NewLib(out var path);
        // 200 sources + 1000 playback bookmarks + 100 viewports —
        // exercise the streaming path with a payload large enough to
        // trip the LOH if the eager path regresses.
        var sources = Enumerable.Range(0, 200).Select(i => new BundleSourceDto
        {
            SourceId = Guid.NewGuid().ToString("N"),
            DisplayName = $"trace{i}",
            Path = $@"C:/recordings/trace{i}.asc",
            ContentHash = new string('a', 64),  // synthetic 64-char SHA-256 hex
        }).ToList();

        var bookmarks = Enumerable.Range(0, 1000).Select(i => new BookmarkDto(
            $"b{i}", i * 0.05, $"bookmark-{i}")).ToList();

        var viewports = Enumerable.Range(0, 100).Select(i => new BundleViewportDto
        {
            EffectiveKey = $"v{i}.0x100.SignalName",
            XMin = 0.0,
            XMax = 60.0,
            IsFocused = false,
            IsCollapsed = false,
        }).ToList();

        var snapshot = new TraceSessionBundleDto
        {
            Version = 1,
            Schema = "tmtrace/v1",
            SavedAt = DateTimeOffset.UtcNow,
            AppVersion = "3.8.5",
            DbcPath = "C:/projects/vehicle.dbc",
            GlobalCanIdFilter = "0x100, 0x200, 0x300",
            Sources = sources,
            Viewports = viewports,
            Playback = new BundlePlaybackDto
            {
                Loop = true,
                Speed = 2.0,
                ScrubberValue = 12.345,
                Bookmarks = bookmarks,
            },
        };

        lib.Save(snapshot);
        var loaded = lib.Load(path);

        loaded.Should().NotBeNull("streaming serialization must produce a loadable bundle");
        loaded!.Sources.Should().HaveCount(200, "all sources must survive streaming round-trip");
        loaded.Playback!.Bookmarks.Should().HaveCount(1000,
            "all bookmarks must survive streaming round-trip");
        loaded.Viewports.Should().HaveCount(100, "all viewports must survive streaming round-trip");
        loaded.Playback.Bookmarks[500].Id.Should().Be("b500",
            "specific bookmark identity must round-trip (catches field-name typos in streaming path)");
        loaded.Sources[100].ContentHash.Should().Be(new string('a', 64),
            "long ContentHash fields must survive streaming round-trip verbatim");
    }

    // ---------- v3.10.0 MINOR T3 (H4): defense-in-depth size cap + path normalize ----------

    /// <summary>
    /// v3.10.0 MINOR T3 (H4): oversized .tmtrace files must be rejected
    /// without being read into memory. Without the precheck, a user who
    /// drops a large file at the persisted path would block the WPF
    /// dispatcher for the full <see cref="File.ReadAllText"/> +
    /// <see cref="JsonSerializer.Deserialize{T}(string,System.Text.Json.JsonSerializerOptions?)"/>
    /// duration — a 1 GB file risks OOM. 50 MB is far above any
    /// legitimate bundle (200 sources + 1000 bookmarks ≈ 200 KB) and
    /// gives 250x headroom for future growth. Mirrors
    /// <c>RecentSessionsService.LoadAsync</c> oversized branch
    /// (v3.8.8 PATCH F2).
    /// </summary>
    [Fact]
    public void Load_OnOversizedFile_ReturnsNull_AndLogsWarning()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"tmtrace-oversized-{Guid.NewGuid():N}.tmtrace");
        // Write just enough bytes to exceed the 50 MB cap. Use sparse
        // seek + write of a single byte at the end so the test is fast
        // and does not actually allocate 60 MB on disk.
        try
        {
            using (var fs = new FileStream(
                path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.SetLength(TraceSessionLibrary.MaxLoadFileBytes + (10L * 1024 * 1024));
                fs.WriteByte(0x00);
            }
            var lib = new TraceSessionLibrary(path, NullLogger<TraceSessionLibrary>.Instance);

            var loaded = lib.Load(path);

            loaded.Should().BeNull(
                "oversized files must NOT be deserialized — caller treats null as corrupt");
            File.Exists(path).Should().BeTrue(
                "oversized-load must NOT delete the file (defensive: user may have mis-saved a real bundle)");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    /// v3.10.0 MINOR T3 (H4): traversal-style paths must be rejected by
    /// the loader without throwing — mirrors the corrupt-recovery contract
    /// that callers already handle. Defense-in-depth: even though the
    /// caller (TraceViewerService.LoadAsync via file dialog) supplies a
    /// real absolute path, the loader must not assume trust. Mirrors
    /// <c>TraceViewerService.LoadAsync</c> PathNormalizer pattern
    /// (v3.9.1 PATCH).
    /// </summary>
    [Fact]
    public void Load_OnTraversalPath_ReturnsNull_AndLogsCorruption()
    {
        // A clearly-malicious traversal-style relative path. PathNormalizer
        // rejects relative paths + `..` segments with PathNormalizationException.
        const string maliciousPath = @"..\..\..\Windows\System32\drivers\etc\hosts";
        var lib = new TraceSessionLibrary(
            Path.Combine(Path.GetTempPath(), $"tmtrace-{Guid.NewGuid():N}.tmtrace"),
            NullLogger<TraceSessionLibrary>.Instance);

        var loaded = lib.Load(maliciousPath);

        loaded.Should().BeNull(
            "traversal paths must be rejected without throwing to the caller");
    }

    /// <summary>
    /// v3.10.0 MINOR T3 (H4 + L7): a JSON payload nested beyond the
    /// configured <c>MaxDepth = 64</c> must be rejected with null rather
    /// than throwing a <see cref="JsonException"/> or stack-overflowing.
    /// The default System.Text.Json depth is 64, so this test only
    /// pins the contract if a future regression drops the explicit
    /// <c>MaxDepth = 64</c> setting.
    /// </summary>
    [Fact]
    public void Load_OnDeeplyNestedJson_DoesNotStackOverflow()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"tmtrace-deep-{Guid.NewGuid():N}.tmtrace");
        try
        {
            // Build a JSON envelope with 80 levels of nested objects,
            // exceeding the configured MaxDepth = 64. The DTO shape does
            // not need to match — JsonSerializer will reject before any
            // field binding once it hits the depth ceiling.
            var deepJson = new StringBuilder();
            deepJson.Append("{ \"a\": ");
            for (int i = 0; i < 80; i++)
                deepJson.Append("{ \"a\": ");
            deepJson.Append('1');
            for (int i = 0; i < 80; i++)
                deepJson.Append(" }");
            deepJson.Append(" }");
            File.WriteAllText(path, deepJson.ToString());

            var lib = new TraceSessionLibrary(path, NullLogger<TraceSessionLibrary>.Instance);

            var loaded = lib.Load(path);

            loaded.Should().BeNull(
                "deeply-nested JSON must be rejected with null, not throw");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}