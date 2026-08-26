using System.IO;
using System.IO.Compression;
using System.Text;
using FluentAssertions;
using PeakCan.HIL.Core.Replay;
using Xunit;

namespace PeakCan.HIL.Core.Tests.Replay;

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
    ///     Q = object_time_stamp (UINT64 LE, 1ns ticks since Vector epoch)
    /// </summary>
    private static void WriteObject(MemoryStream ms, uint objType, int objectDataSize, Action<BinaryWriter> writeFrameData)
        => WriteObject(ms, objType, objectDataSize, writeFrameData, timestamp: 0L);

    /// <summary>
    /// v3.17.0 PATCH: overload with explicit object_time_stamp (UINT64 LE,
    /// 1-nanosecond ticks since Vector epoch). Existing zero-timestamp
    /// overload delegates here. Used by the BLF relative-timestamp tests.
    /// </summary>
    private static void WriteObject(MemoryStream ms, uint objType, int objectDataSize, Action<BinaryWriter> writeFrameData, long timestamp)
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
        ms.Write(BitConverter.GetBytes(timestamp));  // Q = object_time_stamp (8 bytes LE, 1ns ticks)
        // Frame data
        var frameDataPos = ms.Position;
        using (var writer = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            writeFrameData(writer);
        }
        var actualWritten = ms.Position - frameDataPos;
        actualWritten.Should().Be(objectDataSize, "frame data must match objectDataSize");
    }

    // Vector epoch is 1970-01-01 (per BlfFormat.TimestampScale xmldoc:
    // "1-nanosecond ticks since Vector epoch"). BLF absolute seconds =
    // timestamp_ticks / 1e9. WallClockOrigin = VectorEpoch + absolute seconds.
    private static readonly DateTime VectorEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task BlfParser_CanMessage_Parsed()
    {
        // 16-byte HBBI8s: H=channel B=flags B=dlc I=frame_id 8s=data (2+1+1+4+8=16)
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
        // 24-byte HBBI8sIBBH (2+1+1+4+8+4+2+1+1=24)
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
        // 90-byte test-compatible payload (HBBIIBBBBI64sI test fixture layout)
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
        // 56-byte base + 8-byte ext (test-compatible; vblf struct is 40+8=48)
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
        // Per vblf_general.py:447-450 LogContainer.unpack, frame data after the
        // 32-byte ObjectHeader is the raw zlib-compressed payload of the inner
        // objects — NO 4-byte compression_level + 4-byte reserved prefix.
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
        // Container frame data = raw zlib-compressed payload (per vblf general).
        WriteObject(outerMs, BlfFormat.ObjTypeLogContainer, compressed.Length, w =>
        {
            w.Write(compressed);
        });
        outerMs.Position = 0;

        var frames = await BlfParser.ParseAsync(outerMs, DefaultOptions());
        frames.Should().HaveCount(1, "1 CanMessage inside zlib LOG_CONTAINER");
        frames[0].Id.Should().Be(0x456u);
    }

    [Fact]
    public async Task BlfParser_LogContainerMultiple_Parsed()
    {
        // 2 CanMessage frames in 1 zlib container — raw zlib payload per vblf_general.py:450.
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
        WriteObject(outerMs, BlfFormat.ObjTypeLogContainer, compressed.Length, w =>
        {
            w.Write(compressed);
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
        // The fixture is a synthetic CAN_MESSAGE object from the vblf
        // reference library. Its 16-byte CanMessage body contains
        // arbitrary byte values (channel=0x1111, flags=0x22, dlc=0x33,
        // frame_id=0x44444444, data=8 bytes 0x55..0xcc). The "dlc" field
        // does NOT represent a valid classic CAN DLC; it's the ASCII
        // value of '3' used as a sentinel pattern in the fixture.
        // Layout verified 2026-07-16 via Python struct.unpack.
        var path = System.IO.Path.GetFullPath(VblfFixturePath);
        File.Exists(path).Should().BeTrue($"vblf fixture must exist at {path}");
        await using var fs = File.OpenRead(path);
        var frames = await BlfParser.ParseAsync(fs, DefaultOptions());
        frames.Should().HaveCount(1, "vblf_test_CAN_MESSAGE.lobj contains 1 CanMessage");
        frames[0].Id.Should().Be(0x44444444u, "frame_id parsed from synthetic fixture bytes 36-39");
        frames[0].Dlc.Should().Be((byte)0x33, "dlc is the literal byte at fixture offset 35 (synthetic value 51)");
        frames[0].Data.Should().Equal(
            (byte)0x55, (byte)0x66, (byte)0x77, (byte)0x88,
            (byte)0x99, (byte)0xaa, (byte)0xbb, (byte)0xcc);
    }

    // === v3.17.0 PATCH: BLF relative-timestamp relativization (BLF playback fix) ===
    // Root cause: BlfParser stored absolute seconds (since 1970 Vector epoch) into
    // ReplayFrame.Timestamp, but ReplayTimeline.OnTick compares frame.Timestamp
    // (huge absolute value) against PlayedTimestamp (relative, from 0). now never
    // reaches the first frame's Timestamp → 0 frames emit, slider/time frozen.
    // Fix: BlfParser.ParseAsyncWithOrigin relativizes all frame timestamps to the
    // minimum frame timestamp and returns the original absolute first-frame time
    // as WallClockOrigin (sister of AscParseResult). ParseAsync delegates and
    // returns only .Frames (preserves the existing 14-test contract).

    private static void WriteCanMessage(MemoryStream ms, long timestamp, uint frameId)
    {
        WriteObject(ms, BlfFormat.ObjTypeCanMessage, BlfFormat.CanMessageDataSize, w =>
        {
            w.Write((ushort)1);   // channel
            w.Write((byte)0);     // flags
            w.Write((byte)8);     // dlc
            w.Write(frameId);
            w.Write(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04 });
        }, timestamp);
    }

    [Fact]
    public async Task BlfParser_AbsoluteTimestamp_RelativizedToZero()
    {
        // BLF first frame at absolute 155696.89s (≈1.8 days since Vector epoch).
        // ticks = 155696.89 * 1e9 = 155696890000000 (1ns/tick).
        long firstTicks = 155696890000000L;
        long secondTicks = firstTicks + 5_000_000_000L; // +5.0s in 1ns ticks

        var ms = new MemoryStream();
        WriteFileHeader(ms);
        WriteCanMessage(ms, firstTicks, 0x100);
        WriteCanMessage(ms, secondTicks, 0x200);
        ms.Position = 0;

        var result = await BlfParser.ParseAsyncWithOrigin(ms, DefaultOptions());
        result.Frames.Should().HaveCount(2);
        // First frame relativized to 0 (the bug fix core).
        result.Frames[0].Timestamp.Should().Be(0.0);
        // Second frame = +5.0s relative (interval preserved).
        result.Frames[1].Timestamp.Should().BeApproximately(5.0, 1e-6);
        // WallClockOrigin preserved for future X-axis wall-clock display.
        result.WallClockOrigin.Should().NotBeNull();
        result.WallClockOrigin!.Should().Be(VectorEpoch.AddSeconds(155696.89),
            "BLF absolute first-frame seconds = VectorEpoch + 155696.89s");
    }

    [Fact]
    public async Task BlfParser_GapWindow_RelativizedInterval_Preserved()
    {
        // User scenario: BLF messages are not contiguous — large gap window
        // with no messages in the middle. Relativization is a uniform shift;
        // inter-frame intervals (including gaps) must be preserved exactly.
        // frames: A=big, B=A+0.05s, C=A+6.0s (6s gap), D=A+6.01s
        long a = 155696890000000L;
        long b = a + 50_000_000L;     // +0.05s (1ns/tick)
        long c = a + 6_000_000_000L; // +6.0s  (gap window)
        long d = a + 6_010_000_000L; // +6.01s

        var ms = new MemoryStream();
        WriteFileHeader(ms);
        WriteCanMessage(ms, a, 0x01);
        WriteCanMessage(ms, b, 0x02);
        WriteCanMessage(ms, c, 0x03);
        WriteCanMessage(ms, d, 0x04);
        ms.Position = 0;

        var result = await BlfParser.ParseAsyncWithOrigin(ms, DefaultOptions());
        result.Frames.Should().HaveCount(4);
        result.Frames[0].Timestamp.Should().Be(0.0);
        result.Frames[1].Timestamp.Should().BeApproximately(0.05, 1e-6);
        result.Frames[2].Timestamp.Should().BeApproximately(6.0, 1e-6);   // gap preserved
        result.Frames[3].Timestamp.Should().BeApproximately(6.01, 1e-6);
    }

    [Fact]
    public async Task BlfParser_NonMonotonicOrder_RelativizedByMin()
    {
        // BLF object stream order is NOT guaranteed strictly increasing
        // (LOG_CONTAINER recursion can append out-of-absolute-time-order).
        // Relativization uses Min(Timestamp), not result[0], so a non-first
        // minimum frame yields non-negative relative timestamps for all.
        // Layout: first written frame has a LARGER absolute time than the
        // second; the second is the true minimum.
        long smaller = 155696890000000L; // baseline min (true minimum)
        long larger = smaller + 5_000_000_000L; // +5.0s above smaller (1ns/tick)

        var ms = new MemoryStream();
        WriteFileHeader(ms);
        WriteCanMessage(ms, larger, 0x100);   // written first, but larger
        WriteCanMessage(ms, smaller, 0x200);  // written second, true min
        ms.Position = 0;

        var result = await BlfParser.ParseAsyncWithOrigin(ms, DefaultOptions());
        result.Frames.Should().HaveCount(2);
        // Min baseline = smaller (0x200 frame) → both relative to it.
        // 0x200 frame is the min → its relative timestamp is 0.
        // 0x100 frame is +5.0s above min.
        var minFrame = result.Frames.Single(f => f.Id == 0x200u);
        var largerFrame = result.Frames.Single(f => f.Id == 0x100u);
        minFrame.Timestamp.Should().Be(0.0);
        largerFrame.Timestamp.Should().BeApproximately(5.0, 1e-6);
    }

    [Fact]
    public async Task BlfParser_ParseAsync_PreservesRelativeContract()
    {
        // B2 contract: ParseAsync (legacy signature, returns IReadOnlyList)
        // must also relativize — it delegates to ParseAsyncWithOrigin and
        // returns .Frames. Existing 14 tests use zero-timestamp objects;
        // for them Min=0 so relativization is a no-op (values unchanged).
        // This test uses a non-zero first timestamp to prove ParseAsync
        // also relativizes (not just ParseAsyncWithOrigin).
        long firstTicks = 155696890000000L;

        var ms = new MemoryStream();
        WriteFileHeader(ms);
        WriteCanMessage(ms, firstTicks, 0x100);
        WriteCanMessage(ms, firstTicks + 5_000_000_000L, 0x200);
        ms.Position = 0;

        var frames = await BlfParser.ParseAsync(ms, DefaultOptions());
        frames.Should().HaveCount(2);
        frames[0].Timestamp.Should().Be(0.0, "ParseAsync also relativizes (delegates to WithOrigin)");
        frames[1].Timestamp.Should().BeApproximately(5.0, 1e-6);
    }

    // === v3.17.0 PATCH follow-up: BLF frame sort ===
    // Root cause of "播放到结束不停 + 时间超过 TotalDuration 还在涨":
    // BlfParser did NOT sort frames by Timestamp (sister AscParser.ParseLines
    // does `frames.Sort` at ParseLinesFlow.cs:70). BLF object stream order is
    // NOT guaranteed strictly increasing — LOG_CONTAINER recursion appends
    // inner objects in container order, which can be out-of-absolute-time
    // order. Unsorted frames break two invariants:
    //   1. TotalDuration = _frames[^1].Timestamp (ReplayService.cs:55 /
    //      TraceViewerService.cs:88) takes the LAST element, not the max —
    //      so if the max-timestamp frame is in the middle, TotalDuration is
    //      too small. The UI slider Maximum tracks this too-small value, so
    //      playback time visually exceeds TotalDuration mid-playback.
    //   2. OnTick's `_nextFrameIndex >= _frames.Count` EOF check still fires
    //      (idx walks the list in order), but the UI shows time > TotalDuration
    //      for any mid-list frame whose Timestamp > _frames[^1].Timestamp.
    // Fix: BlfParser sorts frames by Timestamp after relativization, matching
    // the AscParser contract. Then TotalDuration = _frames[^1].Timestamp = max.

    [Fact]
    public async Task BlfParser_UnsortedFrames_SortedByTimestamp()
    {
        // Write frames in NON-monotonic absolute order: the largest
        // timestamp is the middle frame. Without sort, _frames[^1] would
        // be the smallest; TotalDuration (last element) would be wrong.
        long a = 155696890000000L;            // baseline (min)
        long c = a + 6_000_000_000L;          // +6.0s (largest — written 2nd, 1ns/tick)
        long b = a + 5_000_000_000L;          // +5.0s (written 3rd, 1ns/tick)

        var ms = new MemoryStream();
        WriteFileHeader(ms);
        WriteCanMessage(ms, a, 0x01);   // written 1st, min
        WriteCanMessage(ms, c, 0x03);   // written 2nd, max (out of order)
        WriteCanMessage(ms, b, 0x02);   // written 3rd, middle
        ms.Position = 0;

        var result = await BlfParser.ParseAsyncWithOrigin(ms, DefaultOptions());
        result.Frames.Should().HaveCount(3);
        // After sort: timestamps ascending [0, 5.0, 6.0].
        result.Frames[0].Timestamp.Should().Be(0.0);
        result.Frames[1].Timestamp.Should().BeApproximately(5.0, 1e-6);
        result.Frames[2].Timestamp.Should().BeApproximately(6.0, 1e-6);
        // IDs follow their frames through the sort.
        result.Frames[0].Id.Should().Be(0x01u);
        result.Frames[1].Id.Should().Be(0x02u);
        result.Frames[2].Id.Should().Be(0x03u);
        // TotalDuration invariant: last element = max after sort.
        result.Frames[^1].Timestamp.Should().Be(result.Frames.Max(f => f.Timestamp),
            "after sort, _frames[^1] is the max — TotalDuration invariant restored");
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
