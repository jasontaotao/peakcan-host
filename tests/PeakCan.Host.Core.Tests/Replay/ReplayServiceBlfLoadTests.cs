// v3.51.0 MINOR end-to-end smoke: build a synthetic .blf file on
// disk, call ReplayService.LoadAsync (the EXACT entry point that
// OpenAsync in ReplayViewModel uses), then read total duration +
// frame count via public IReplayService surface. Asserts the
// dispatcher in FileIoLifecycle.partial.cs:44 routes ".blf" to
// BlfParser at the service boundary, NOT just inside the parser
// unit-test seam. Sister of AscParserTests smoke patterns.
using System.IO;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Replay;

public class ReplayServiceBlfLoadTests
{
    [Fact]
    public async Task LoadAsync_OnBlfFile_RoutesToBlfParser_AndPopulatesFrames()
    {
        // Arrange — build a tiny .blf with one CanMessage object (12-byte
        // payload). Layout per BlfFormat constants + BlfParserTests
        // WriteObject: 24-byte file header + 32-byte ObjectHeader (4 LOBJ +
        // 16 ObjectHeaderBase + 16 ObjectHeader ext) + 12 frame bytes.
        var blfPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"blf-smoke-{Guid.NewGuid():N}.blf");
        try
        {
            BuildSyntheticBlf(blfPath);

            // v3.51.0: sink is unused for LoadAsync but ReplayService ctor
            // requires one. NSubstitute a no-op.
            IReplayFrameSink sink = Substitute.For<IReplayFrameSink>();
            var sut = new ReplayService(sink, NullLogger<ReplayService>.Instance);

            // Act
            await sut.LoadAsync(blfPath);

            // Assert — dispatcher picked .blf → BlfParser and populated frames.
            sut.Frames.Should().HaveCount(1,
                "the synthetic .blf has exactly 1 CanMessage; if count is 0 the dispatcher routed to AscParser instead");
            sut.TotalDuration.Should().BeGreaterThan(0,
                "the synthetic frame has a non-zero timestamp; 0 means parse failed silently");
            sut.Frames[0].Id.Should().Be(0x123u,
                "frame_id round-trips through BlfParser correctly");
        }
        finally
        {
            if (System.IO.File.Exists(blfPath)) System.IO.File.Delete(blfPath);
        }
    }

    private static void BuildSyntheticBlf(string path)
    {
        using var ms = new MemoryStream();
        // File header: 24 bytes
        ms.Write(Encoding.ASCII.GetBytes(BlfFormat.FileSignature));
        ms.Write(new byte[BlfFormat.FileHeaderSize - 4]);
        // ObjectHeader (32): 4 LOBJ + 2 header_size + 2 header_version
        // + 4 object_size + 4 object_type + 4 reserved + 2 client +
        // 2 reserved + 8 timestamp
        ms.Write(Encoding.ASCII.GetBytes(BlfFormat.ObjSignature));
        ms.Write(BitConverter.GetBytes((ushort)BlfFormat.ObjectHeaderSize));
        ms.Write(BitConverter.GetBytes((ushort)1));
        var totalObjBytes = (uint)BlfFormat.ObjectHeaderSize + (uint)BlfFormat.CanMessageDataSize;
        ms.Write(BitConverter.GetBytes(totalObjBytes));  // 32+12=44
        ms.Write(BitConverter.GetBytes(BlfFormat.ObjTypeCanMessage));
        ms.Write(new byte[BlfFormat.ObjectHeaderSize - 16]);  // pad rest to 32 bytes
        ms.Position -= (BlfFormat.ObjectHeaderSize - 16);
        // Now overwrite ObjectHeader extension fields (per Task T1 verify):
        // I object_flags (4) + H client_index (2) + H reserved (2) + Q timestamp (8) = 16
        ms.Write(BitConverter.GetBytes(0u));
        ms.Write(BitConverter.GetBytes((ushort)0));
        ms.Write(BitConverter.GetBytes((ushort)0));
        // Q timestamp = 0.5 seconds = 5_000_000 in 10ns ticks
        ms.Write(BitConverter.GetBytes(5_000_000L));

        // Frame data (12 bytes HBBI8s)
        ms.Write(BitConverter.GetBytes((ushort)1));    // channel
        ms.WriteByte(0);                               // flags
        ms.WriteByte(8);                               // dlc
        ms.Write(BitConverter.GetBytes(0x123u));       // frame_id
        ms.Write(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04 });

        System.IO.File.WriteAllBytes(path, ms.ToArray());
    }
}
