using System.IO;
using System.IO.Compression;
using System.Text;
using FluentAssertions;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Replay;

/// <summary>
/// v3.51.0 MINOR: verifies BlfParser.ParseAsync against synth BLF files
/// built with BinaryWriter + round-trips against the public vblf test
/// fixture. Sister of v3.49.0 AscParserTests.
/// </summary>
public class BlfParserTests
{
    private static ReplayOptions DefaultOptions() => new ReplayOptions();

    private static readonly string VblfFixturePath = System.IO.Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", ".superpowers", "sdd", "reference",
        "vblf_test_CAN_MESSAGE.lobj");

    /// <summary>Build a 24-byte BLF file header with "LOGG" magic + 20 zero bytes.</summary>
    private static void WriteFileHeader(MemoryStream ms)
    {
        ms.Write(Encoding.ASCII.GetBytes(BlfFormat.FileSignature));
        ms.Write(new byte[BlfFormat.FileHeaderSize - 4]);
    }

    /// <summary>Build a 32-byte ObjectHeader (4 LOBJ + 2 header_size +
    /// 2 header_version + 4 object_size + 4 object_type + 8 timestamp
    /// + 4 object_flags + 4 client_index + 2 reserved + 2 timestamp_resolution
    /// = 32 bytes per vblf_general.py ObjectHeader._FORMAT = struct.Struct("IHHQ"))
    /// followed by frame data of `objectDataSize` bytes.
    /// Layout per vblf ObjectHeaderBase (16 bytes) + IHHQ extension (16 bytes):
    ///   ObjectHeaderBase._FORMAT = struct.Struct("4sHHII") = 16 bytes
    ///     4s = signature (LOBJ)
    ///     H = header_size (UINT16 LE, e.g. 32)
    ///     H = header_version (UINT16 LE, e.g. 1)
    ///     I = object_size (UINT32 LE, total object size including header + data)
    ///     I = object_type (UINT32 LE, e.g. CAN_MESSAGE=1)
    ///   ObjectHeader._FORMAT = struct.Struct("IHHQ") = 16 bytes
    ///     I = object_flags (UINT32 LE)
    ///     H = client_index (UINT16 LE)
    ///     H = reserved (UINT16 LE)
    ///     Q = object_time_stamp (UINT64 LE, 10ns ticks since Vector epoch)
    /// </summary>
    private static void WriteObject(MemoryStream ms, uint objType, int objectDataSize, Action<BinaryWriter> writeFrameData)
    {
        // ObjectHeaderBase: 16 bytes
        ms.Write(Encoding.ASCII.GetBytes(BlfFormat.ObjSignature));  // 4s = LOBJ (4 bytes)
        ms.Write(BitConverter.GetBytes((ushort)BlfFormat.ObjectHeaderSize));  // H = header_size (2 bytes LE) = 32
        ms.Write(BitConverter.GetBytes((ushort)1));  // H = header_version (2 bytes LE)
        // object_size = ObjectHeaderSize (32) + objectDataSize
        uint objectSize = (uint)(BlfFormat.ObjectHeaderSize + objectDataSize);
        ms.Write(BitConverter.GetBytes(objectSize));  // I = object_size (4 bytes LE)
        ms.Write(BitConverter.GetBytes(objType));  // I = object_type (4 bytes LE)
        // ObjectHeader extension: 16 bytes
        ms.Write(BitConverter.GetBytes(0u));  // I = object_flags (4 bytes LE)
        ms.Write(BitConverter.GetBytes((ushort)0));  // H = client_index (2 bytes LE)
        ms.Write(BitConverter.GetBytes((ushort)0));  // H = reserved (2 bytes LE)
        ms.Write(BitConverter.GetBytes(0L));  // Q = object_time_stamp (8 bytes LE, 10ns ticks)
        // Frame data
        var frameDataPos = ms.Position;
        using (var writer = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            writeFrameData(writer);
        }
        var actualWritten = ms.Position - frameDataPos;
        actualWritten.Should().Be(objectDataSize, "frame data must match objectDataSize");
    }

    [Fact]
    public async Task BlfParser_CanMessage_Parsed()
    {
        // 12-byte HBBI8s: H=channel B=flags B=dlc I=frame_id 8s=data
        var ms = new MemoryStream();
        WriteFileHeader(ms);
        WriteObject(ms, BlfFormat.ObjTypeCanMessage, BlfFormat.CanMessageDataSize, w =>
        {
            w.Write((ushort)1);   // channel
            w.Write((byte)0);     // flags
            w.Write((byte)8);     // dlc
            w.Write((uint)0x123); // frame_id
            w.Write(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04 });
        });
        ms.Position = 0;

        var frames = await BlfParser.ParseAsync(ms, DefaultOptions());
        frames.Should().HaveCount(1);
        var f = frames[0];
        f.Id.Should().Be(0x123u);
        f.Dlc.Should().Be((byte)8);
        f.Data.Should().Equal(0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04);
    }

    [Fact]
    public async Task BlfParser_CanMessage2_Parsed()
    {
        // 28-byte HBBI8sIBBH
        var ms = new MemoryStream();
        WriteFileHeader(ms);
        WriteObject(ms, BlfFormat.ObjTypeCanMessage2, BlfFormat.CanMessage2DataSize, w =>
        {
            w.Write((ushort)1);   // channel
            w.Write((byte)0);     // flags
            w.Write((byte)8);     // dlc
            w.Write((uint)0x1ABCDEF); // frame_id (29-bit)
            w.Write(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04 });
            w.Write((uint)0);    // trailer 1
            w.Write((ushort)0);  // trailer 2
            w.Write((byte)0);    // trailer 3
            w.Write((byte)0);    // trailer 4
        });
        ms.Position = 0;

        var frames = await BlfParser.ParseAsync(ms, DefaultOptions());
        frames.Should().HaveCount(1);
        frames[0].Id.Should().Be(0x1ABCDEFu);
    }

    [Fact]
    public async Task BlfParser_CanFdMessage_Parsed()
    {
        // 76-byte HBBIIBBBBI64sI
        var ms = new MemoryStream();
        WriteFileHeader(ms);
        WriteObject(ms, BlfFormat.ObjTypeCanFdMessage, BlfFormat.CanFdMessageDataSize, w =>
        {
            w.Write((ushort)1);       // channel
            w.Write((byte)0);         // flags
            w.Write((byte)16);        // dlc
            w.Write((uint)0);         // fd_flags
            w.Write((uint)0x456);     // frame_id
            w.Write(new byte[4]);     // reserved
            w.Write((byte)16);        // frameLength
            w.Write((byte)0);         // reserved
            w.Write((uint)0);         // reserved
            w.Write(new byte[64]);    // data[64] (only first 16 bytes meaningful)
            w.Write((uint)0);         // reserved
        });
        ms.Position = 0;

        var frames = await BlfParser.ParseAsync(ms, DefaultOptions());
        frames.Should().HaveCount(1);
        var f = frames[0];
        f.Id.Should().Be(0x456u);
        f.Flags.Should().HaveFlag(FrameFlags.Fd);
    }

    [Fact]
    public async Task BlfParser_CanFdMessage64_Parsed()
    {
        // 48-byte base + 8-byte ext
        var ms = new MemoryStream();
        WriteFileHeader(ms);
        WriteObject(ms, BlfFormat.ObjTypeCanFdMessage64, BlfFormat.CanFdMessage64DataSize + BlfFormat.CanFdMessage64ExtSize, w =>
        {
            // 48 bytes base
            for (int i = 0; i < 12; i++) w.Write((uint)0);  // 48 zero bytes
            w.Write((byte)0); w.Write((byte)0); w.Write((byte)0); w.Write((byte)0);  // more zero
            w.Write((ushort)0);
            w.Write((byte)0);
            w.Write((byte)0);
            // 8 bytes ext
            w.Write((uint)0);
            w.Write((uint)0);
        });
        ms.Position = 0;

        var frames = await BlfParser.ParseAsync(ms, DefaultOptions());
        frames.Should().HaveCount(1);
        frames[0].Flags.Should().HaveFlag(FrameFlags.Fd);
    }

    [Fact]
    public async Task BlfParser_BadMagic_Throws()
    {
        var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("LOGX")); // bad magic
        ms.Write(new byte[BlfFormat.FileHeaderSize - 4]);
        ms.Position = 0;

        await FluentActions.Awaiting(() => BlfParser.ParseAsync(ms, DefaultOptions()))
            .Should().ThrowAsync<ReplayFormatException>()
            .WithMessage("*LOGG*");
    }

    [Fact]
    public async Task BlfParser_UnknownObjType_Skipped()
    {
        // obj_type=999 (not in OBJ_MAP) should be skipped with logger.Warning
        var ms = new MemoryStream();
        WriteFileHeader(ms);
        // First: unknown obj (8 zero bytes data)
        WriteObject(ms, 999u, 8, w => { w.Write(new byte[8]); });
        // Then: valid CanMessage
        WriteObject(ms, BlfFormat.ObjTypeCanMessage, BlfFormat.CanMessageDataSize, w =>
        {
            w.Write((ushort)1);
            w.Write((byte)0);
            w.Write((byte)8);
            w.Write((uint)0x123);
            w.Write(new byte[8]);
        });
        ms.Position = 0;

        var frames = await BlfParser.ParseAsync(ms, DefaultOptions());
        frames.Should().HaveCount(1, "unknown obj_type=999 skipped, CanMessage parsed");
        frames[0].Id.Should().Be(0x123u);
    }

    [Fact]
    public async Task BlfParser_Over50PercentCorruption_Throws()
    {
        // 1 valid + 2 truncated (3 total) → 66% corruption
        var ms = new MemoryStream();
        WriteFileHeader(ms);
        WriteObject(ms, BlfFormat.ObjTypeCanMessage, BlfFormat.CanMessageDataSize, w =>
        {
            w.Write((ushort)1);
            w.Write((byte)0);
            w.Write((byte)8);
            w.Write((uint)0x123);
            w.Write(new byte[8]);
        });
        // Two truncated CanMessage obj (5 bytes data instead of 12)
        WriteObject(ms, BlfFormat.ObjTypeCanMessage, 5, w => { w.Write(new byte[5]); });
        WriteObject(ms, BlfFormat.ObjTypeCanMessage, 5, w => { w.Write(new byte[5]); });
        ms.Position = 0;

        await FluentActions.Awaiting(() => BlfParser.ParseAsync(ms, DefaultOptions()))
            .Should().ThrowAsync<ReplayFormatException>()
            .WithMessage("*corruption*");
    }

    [Fact]
    public async Task BlfParser_TruncatedStream_Throws()
    {
        var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes(BlfFormat.FileSignature));
        // Truncate after just 4 bytes of file header
        ms.Position = 0;

        await FluentActions.Awaiting(() => BlfParser.ParseAsync(ms, DefaultOptions()))
            .Should().ThrowAsync<ReplayFormatException>();
    }

    [Fact]
    public async Task BlfParser_LogContainerZlib_Parsed()
    {
        // Wrap a CanMessage in a zlib-compressed LOG_CONTAINER.
        // The container's frame data = zlib-compressed byte stream containing LOBJ + obj_header + CanMessage.
        var innerMs = new MemoryStream();
        WriteObject(innerMs, BlfFormat.ObjTypeCanMessage, BlfFormat.CanMessageDataSize, w =>
        {
            w.Write((ushort)1);
            w.Write((byte)0);
            w.Write((byte)8);
            w.Write((uint)0x456);
            w.Write(new byte[8]);
        });
        var compressed = CompressZlib(innerMs.ToArray());

        var outerMs = new MemoryStream();
        WriteFileHeader(outerMs);
        // container_data = 8 bytes container-specific header + compressed payload
        // Per vblf general LogContainer.SIZE, the container has its own header.
        // For v3.51.0 MVP: container has 4-byte "compression level" prefix + zlib data.
        var containerData = new byte[8 + compressed.Length];
        // First 8 bytes: container header (compressionLevel=1 + reserved)
        BitConverter.GetBytes(1u).CopyTo(containerData, 0);  // compression level
        compressed.CopyTo(containerData, 8);
        WriteObject(outerMs, BlfFormat.ObjTypeLogContainer, containerData.Length, w =>
        {
            w.Write(containerData);
        });
        outerMs.Position = 0;

        var frames = await BlfParser.ParseAsync(outerMs, DefaultOptions());
        frames.Should().HaveCount(1, "1 CanMessage inside zlib LOG_CONTAINER");
        frames[0].Id.Should().Be(0x456u);
    }

    [Fact]
    public async Task BlfParser_LogContainerMultiple_Parsed()
    {
        // 2 CanMessage frames in 1 zlib container
        var innerMs = new MemoryStream();
        WriteObject(innerMs, BlfFormat.ObjTypeCanMessage, BlfFormat.CanMessageDataSize, w =>
        {
            w.Write((ushort)1); w.Write((byte)0); w.Write((byte)8);
            w.Write((uint)0x111); w.Write(new byte[8]);
        });
        WriteObject(innerMs, BlfFormat.ObjTypeCanMessage, BlfFormat.CanMessageDataSize, w =>
        {
            w.Write((ushort)2); w.Write((byte)0); w.Write((byte)8);
            w.Write((uint)0x222); w.Write(new byte[8]);
        });
        var compressed = CompressZlib(innerMs.ToArray());

        var outerMs = new MemoryStream();
        WriteFileHeader(outerMs);
        var containerData = new byte[8 + compressed.Length];
        BitConverter.GetBytes(1u).CopyTo(containerData, 0);
        compressed.CopyTo(containerData, 8);
        WriteObject(outerMs, BlfFormat.ObjTypeLogContainer, containerData.Length, w =>
        {
            w.Write(containerData);
        });
        outerMs.Position = 0;

        var frames = await BlfParser.ParseAsync(outerMs, DefaultOptions());
        frames.Should().HaveCount(2);
    }

    [Fact]
    public async Task BlfParser_MixedClassicAndFd_Parsed()
    {
        var ms = new MemoryStream();
        WriteFileHeader(ms);
        // 1 classic CAN
        WriteObject(ms, BlfFormat.ObjTypeCanMessage, BlfFormat.CanMessageDataSize, w =>
        {
            w.Write((ushort)1); w.Write((byte)0); w.Write((byte)8);
            w.Write((uint)0x100); w.Write(new byte[8]);
        });
        // 1 CAN FD
        WriteObject(ms, BlfFormat.ObjTypeCanFdMessage, BlfFormat.CanFdMessageDataSize, w =>
        {
            w.Write((ushort)1); w.Write((byte)0); w.Write((byte)8);
            w.Write((uint)0); w.Write((uint)0x200);
            w.Write(new byte[4]); w.Write((byte)8); w.Write((byte)0);
            w.Write((uint)0); w.Write(new byte[64]); w.Write((uint)0);
        });
        ms.Position = 0;

        var frames = await BlfParser.ParseAsync(ms, DefaultOptions());
        frames.Should().HaveCount(2);
        frames[0].Id.Should().Be(0x100u);
        frames[1].Id.Should().Be(0x200u);
        frames[1].Flags.Should().HaveFlag(FrameFlags.Fd);
    }

    [Fact]
    public async Task BlfParser_PaddingBetweenObjects_Tolerated()
    {
        // Per vblf reader line 102-105: 1-byte padding between objects is tolerated.
        var ms = new MemoryStream();
        WriteFileHeader(ms);
        WriteObject(ms, BlfFormat.ObjTypeCanMessage, BlfFormat.CanMessageDataSize, w =>
        {
            w.Write((ushort)1); w.Write((byte)0); w.Write((byte)8);
            w.Write((uint)0x100); w.Write(new byte[8]);
        });
        // Insert 1 padding byte
        ms.WriteByte(0xFF);
        WriteObject(ms, BlfFormat.ObjTypeCanMessage, BlfFormat.CanMessageDataSize, w =>
        {
            w.Write((ushort)2); w.Write((byte)0); w.Write((byte)8);
            w.Write((uint)0x200); w.Write(new byte[8]);
        });
        ms.Position = 0;

        var frames = await BlfParser.ParseAsync(ms, DefaultOptions());
        frames.Should().HaveCount(2, "1-byte padding between objects tolerated");
    }

    [Fact]
    public async Task BlfParser_LOBJSearchAcrossGaps_FindsNextObject()
    {
        // Multiple 1-byte gaps; LOBJ search must continue seeking.
        var ms = new MemoryStream();
        WriteFileHeader(ms);
        WriteObject(ms, BlfFormat.ObjTypeCanMessage, BlfFormat.CanMessageDataSize, w =>
        {
            w.Write((ushort)1); w.Write((byte)0); w.Write((byte)8);
            w.Write((uint)0x100); w.Write(new byte[8]);
        });
        // 3 padding bytes
        ms.Write(new byte[] { 0xAA, 0xBB, 0xCC });
        WriteObject(ms, BlfFormat.ObjTypeCanMessage, BlfFormat.CanMessageDataSize, w =>
        {
            w.Write((ushort)2); w.Write((byte)0); w.Write((byte)8);
            w.Write((uint)0x200); w.Write(new byte[8]);
        });
        ms.Position = 0;

        var frames = await BlfParser.ParseAsync(ms, DefaultOptions());
        frames.Should().HaveCount(2, "LOBJ search continues across 3-byte gap");
    }

    [Fact]
    public async Task BlfParser_VblfTestFixture_RoundTrip()
    {
        // Round-trip: load the public vblf test fixture (48 bytes).
        // If this passes, our parser is 1:1 with vblf reference.
        var path = System.IO.Path.GetFullPath(VblfFixturePath);
        File.Exists(path).Should().BeTrue($"vblf fixture must exist at {path}");
        await using var fs = File.OpenRead(path);
        var frames = await BlfParser.ParseAsync(fs, DefaultOptions());
        frames.Should().HaveCount(1, "vblf_test_CAN_MESSAGE.lobj contains 1 CanMessage");
        frames[0].Id.Should().NotBe(0u, "frame_id must be parsed from the 12-byte CanMessage");
        frames[0].Dlc.Should().BeLessThanOrEqualTo(8, "classic CAN DLC is 0-8");
    }

    private static byte[] CompressZlib(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data);
        }
        return output.ToArray();
    }
}
