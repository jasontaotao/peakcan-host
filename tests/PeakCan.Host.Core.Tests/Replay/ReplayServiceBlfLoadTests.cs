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
        // 24-byte file header
        ms.Write(Encoding.ASCII.GetBytes(BlfFormat.FileSignature));
        ms.Write(new byte[BlfFormat.FileHeaderSize - 4]);
        // ObjectHeader (32) in EXACT field order matching
        // BlfParser.ParseObjectHeader (LOBJ + IHHQ extension):
        // 4 LOBJ + 2 header_size + 2 header_version + 4 objSize +
        // 4 objType + 4 object_flags + 2 client + 2 reserved + 8 timestamp.
        // The earlier draft inserted 16 zero bytes between the first 16
        // and the IHHQ extension, placing the timestamp 24 bytes too
        // far forward — TotalDuration came back 0 even though
        // LoadedFrames.Count==1. v3.51.0 T5 PATCH lines up the order.
        ms.Write(Encoding.ASCII.GetBytes(BlfFormat.ObjSignature));
        ms.Write(BitConverter.GetBytes((ushort)BlfFormat.ObjectHeaderSize));
        ms.Write(BitConverter.GetBytes((ushort)1));
        var totalObjBytes = (uint)BlfFormat.ObjectHeaderSize + (uint)BlfFormat.CanMessageDataSize;
        ms.Write(BitConverter.GetBytes(totalObjBytes));
        ms.Write(BitConverter.GetBytes(BlfFormat.ObjTypeCanMessage));
        ms.Write(BitConverter.GetBytes(0u));            // object_flags (4)
        ms.Write(BitConverter.GetBytes((ushort)0));     // client_index (2)
        ms.Write(BitConverter.GetBytes((ushort)0));     // reserved (2)
        ms.Write(BitConverter.GetBytes(5_000_000L));    // timestamp 0.5s
        // 12-byte CanMessage frame data
        ms.Write(BitConverter.GetBytes((ushort)1));
        ms.WriteByte(0);
        ms.WriteByte(8);
        ms.Write(BitConverter.GetBytes(0x123u));
        ms.Write(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04 });

        System.IO.File.WriteAllBytes(path, ms.ToArray());
    }
}
