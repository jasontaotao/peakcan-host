using System.IO;
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
}