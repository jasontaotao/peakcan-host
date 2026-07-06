using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Logging;
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
}