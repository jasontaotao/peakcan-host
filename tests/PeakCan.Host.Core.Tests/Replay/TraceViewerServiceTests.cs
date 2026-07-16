using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.Core.Replay;
using Xunit;

// Note: System.IO.Path is fully qualified below to avoid resolving to
// PeakCan.Host.Core.Tests.Path (shadowing via sibling test files for
// PathNormalizer). Same defense-in-depth as PeakCan.Host.Core.Path.

namespace PeakCan.Host.Core.Tests.Replay;

public class TraceViewerServiceTests
{
    // Static readonly to satisfy CA1861 (constant array arg).
    // ASC format: timestamp channel id dlc dataBytes — matches AscParser test fixtures.
    private static readonly string[] TwoFrameAsc = new[]
    {
        "   0.000000 51  100  8  11 22 33 44 55 66 77 88",
        "   1.000000 51  100  8  AA BB CC DD EE FF 00 11",
    };

    [Fact]
    public void Constructor_DoesNotAcceptIReplayFrameSink()
    {
        // The whole point of TraceViewerService is to be a sibling of
        // ReplayService that does NOT write to the bus. If a future refactor
        // accidentally adds an IReplayFrameSink ctor param, this fails.
        var sinkType = typeof(IReplayFrameSink);
        var ctors = typeof(TraceViewerService).GetConstructors(
            BindingFlags.Public | BindingFlags.Instance);

        foreach (var ctor in ctors)
        {
            var paramTypes = ctor.GetParameters().Select(p => p.ParameterType);
            paramTypes.Should().NotContain(sinkType,
                "TraceViewerService must never depend on IReplayFrameSink");
        }
    }

    [Fact]
    public async Task LoadAsync_ValidAsc_SetsTotalDuration()
    {
        // Build a 2-frame ASC in a temp file
        var path = System.IO.Path.GetTempFileName();
        await File.WriteAllLinesAsync(path, TwoFrameAsc);
        try
        {
            var sut = new TraceViewerService(Substitute.For<ILogger<TraceViewerService>>());
            await sut.LoadAsync(path);
            sut.TotalDuration.Should().Be(1.0);
        }
        finally { File.Delete(path); }
    }

    // v3.51.0 MINOR T5: Verify TraceViewerService dispatches .blf to
    // BlfParser (sister of ReplayService/FileIoLifecycle.partial.cs:44).
    // Before this test, TraceViewer was .asc-only — the v3.51.0 spec
    // assumed dispatcher would be added in T3 but the TraceViewer
    // service was missed in the T3 audit. T5 PATCH closes the gap.
    [Fact]
    public async Task LoadAsync_ValidBlf_RoutesToBlfParserAndPopulatesFrames()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tv-blf-{Guid.NewGuid():N}.blf");
        try
        {
            // Build a synthetic 1-frame CanMessage .blf
            BuildSyntheticBlf(path);

            var sut = new TraceViewerService(Substitute.For<ILogger<TraceViewerService>>());
            await sut.LoadAsync(path);

            sut.LoadedFrames.Should().HaveCount(1,
                "synthetic .blf has exactly 1 CanMessage; if 0 the .blf branch did not run");
            sut.TotalDuration.Should().BeGreaterThan(0,
                "non-zero timestamp → non-zero TotalDuration");
            sut.LoadedFrames[0].Id.Should().Be(0x123u);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static void BuildSyntheticBlf(string path)
    {
        using var ms = new System.IO.MemoryStream();
        // 24-byte file header
        ms.Write(System.Text.Encoding.ASCII.GetBytes(BlfFormat.FileSignature));
        ms.Write(new byte[BlfFormat.FileHeaderSize - 4]);
        // ObjectHeader (32): 4 LOBJ + 2 header_size + 2 header_version +
        // 4 object_size + 4 object_type + 4 object_flags + 2 client +
        // 2 reserved + 8 timestamp. Field order matches
        // BlfParser.ParseObjectHeader exactly: the previous version of
        // this helper inserted 16 zero bytes between ObjectHeaderBase
        // (size 16) and the IHHQ extension (size 16), but that placed
        // the timestamp at file offset +48 from LOBJ instead of +24,
        // making TotalDuration look 0 even though the frame round-tripped.
        // v3.51.0 T5 PATCH fix.
        ms.Write(System.Text.Encoding.ASCII.GetBytes(BlfFormat.ObjSignature));
        ms.Write(BitConverter.GetBytes((ushort)BlfFormat.ObjectHeaderSize));
        ms.Write(BitConverter.GetBytes((ushort)1));
        var totalObjBytes = (uint)BlfFormat.ObjectHeaderSize + (uint)BlfFormat.CanMessageDataSize;
        ms.Write(BitConverter.GetBytes(totalObjBytes));
        ms.Write(BitConverter.GetBytes(BlfFormat.ObjTypeCanMessage));
        ms.Write(BitConverter.GetBytes(0u));            // object_flags (4)
        ms.Write(BitConverter.GetBytes((ushort)0));     // client_index (2)
        ms.Write(BitConverter.GetBytes((ushort)0));     // reserved (2)
        ms.Write(BitConverter.GetBytes(5_000_000L));    // timestamp 0.5s (Q = 8)
        // 12-byte CanMessage frame
        ms.Write(BitConverter.GetBytes((ushort)1));
        ms.WriteByte(0);
        ms.WriteByte(8);
        ms.Write(BitConverter.GetBytes(0x123u));
        ms.Write(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04 });

        File.WriteAllBytes(path, ms.ToArray());
    }

    [Fact]
    public async Task LoadAsync_FileNotFound_ThrowsReplayLoadException()
    {
        var sut = new TraceViewerService(Substitute.For<ILogger<TraceViewerService>>());
        var act = () => sut.LoadAsync(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "does-not-exist.asc"));
        await act.Should().ThrowAsync<ReplayLoadException>();
    }

    [Fact]
    public async Task LoadAsync_EmptyFile_ThrowsReplayFormatException()
    {
        // v3.9.1 PATCH Bug #2: empty .asc files must surface as parse errors,
        // not silently load as 0 frames. Pre-fix, TraceViewerService.LoadAsync
        // line 62-68 caught ReplayFormatException when _frames.Count==0 and
        // silently set frames to Array.Empty — user saw no error at all.
        var path = System.IO.Path.GetTempFileName();
        await File.WriteAllTextAsync(path, "");  // empty
        try
        {
            var sut = new TraceViewerService(Substitute.For<ILogger<TraceViewerService>>());
            var act = () => sut.LoadAsync(path);
            await act.Should().ThrowAsync<ReplayFormatException>(
                "v3.9.1 PATCH: empty .asc files must surface as parse errors, not silently load as 0 frames");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task LoadAsync_FileExceedsSizeCap_ThrowsReplayLoadException()
    {
        // v3.9.1 PATCH Bug #2 size-cap guard (mirrors v3.8.8 PATCH F2 pattern).
        // Files larger than MaxAscFileBytes (200 MB) are rejected BEFORE
        // File.OpenRead + AscParser.ParseAsync, preventing OOM and the
        // multi-second UI freeze that the multi-GB import bug produced.
        // We use FileStream.SetLength to create a sparse 201 MB file (cheap
        // on NTFS — no actual disk allocation).
        var path = System.IO.Path.GetTempFileName();
        try
        {
            using (var fs = File.OpenWrite(path))
            {
                fs.SetLength(TraceViewerService.MaxAscFileBytes + 1);
            }
            var sut = new TraceViewerService(Substitute.For<ILogger<TraceViewerService>>());
            var act = () => sut.LoadAsync(path);
            var ex = await act.Should().ThrowAsync<ReplayLoadException>(
                "v3.9.1 PATCH: files beyond MaxAscFileBytes must be rejected with ReplayLoadException");
            ex.And.Message.Should().Contain("exceeds size cap");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task LoadAsync_ValidAsc_ExposesLoadedFrames()
    {
        // After loading a 2-frame ASC, LoadedFrames must surface both frames
        // with their parsed CAN IDs intact so the VM can iterate them for
        // per-signal DBC decode in Task 2.
        var path = System.IO.Path.GetTempFileName();
        await File.WriteAllLinesAsync(path, TwoFrameAsc);
        try
        {
            var sut = new TraceViewerService(Substitute.For<ILogger<TraceViewerService>>());
            await sut.LoadAsync(path);

            sut.LoadedFrames.Should().HaveCount(2);
            sut.LoadedFrames[0].Id.Should().Be(0x100u);
            sut.LoadedFrames[1].Id.Should().Be(0x100u);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Play_PreLoad_DoesNotEmit()
    {
        // Sanity check: subscribing to FrameEmitted and never invoking Load
        // (or Play) must produce no frames. This is the precondition that the
        // real Play → FrameEmitted round-trip test in Task 5 will rely on.
        //
        // The full Play loop is intentionally NOT exercised here — that needs a
        // real ASC and is the responsibility of the Task 5 TraceViewerViewModel
        // integration tests.
        //
        // The "no sink" property is enforced separately by
        // Constructor_DoesNotAcceptIReplayFrameSink (reflection over ctors).
        var sut = new TraceViewerService(Substitute.For<ILogger<TraceViewerService>>());
        ReplayFrame? emitted = null;
        sut.FrameEmitted += f => emitted = f;

        // Load + Play not invoked → no frames have been emitted.
        emitted.Should().BeNull();
        sut.State.Should().Be(ReplayState.Stopped);
    }

    /// <summary>
    /// v3.18.0 PATCH: TraceViewerService must expose the parsed
    /// AscParseResult (or at minimum the WallClockOrigin) so the
    /// caller can bind the origin to the source. The service has
    /// no registry reference; binding is the caller's job.
    /// </summary>
    [Fact]
    public async Task LastParseResult_AfterLoadAsync_ExposesWallClockOrigin()
    {
        var svc = new TraceViewerService(NullLogger<TraceViewerService>.Instance);
        svc.LastParseResult.Should().BeNull(
            "before any load, the result is null");

        // After a LoadAsync against a tiny inline ASC,
        // LastParseResult must carry the origin.
        const string asc = @"
date Wed Jul 1 08:32:01.000 am 2026
base hex  timestamps absolute
 0.000000 1 100 2 01 02
";
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"trace-{Guid.NewGuid():N}.asc");
        await System.IO.File.WriteAllTextAsync(path, asc);
        try
        {
            await svc.LoadAsync(path);
            svc.LastParseResult.Should().NotBeNull();
            svc.LastParseResult!.WallClockOrigin.Should().Be(
                new DateTime(2026, 7, 1, 8, 32, 1, DateTimeKind.Local));
        }
        finally
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
    }
}