using System.IO;
using System.Text;
using FluentAssertions;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Replay;

/// <summary>
/// v3.51.0 MINOR: verifies BlfFormat constants match the vblf reference
/// 1:1. Sister of v3.49.0 AscFormatRoundTripTests. Constants verified
/// against `.superpowers/sdd/reference/vblf_constants.py` + `vblf_can.py`.
/// </summary>
public class BlfFormatTests
{
    private static readonly string VblfFixturePath = System.IO.Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", ".superpowers", "sdd", "reference",
        "vblf_test_CAN_MESSAGE.lobj");

    [Fact]
    public void BlfFormat_FileSignatureIsLogg()
    {
        BlfFormat.FileSignature.Should().Be("LOGG",
            "vblf_constants.py line 4: FILE_SIGNATURE: Final = b\"LOGG\"");
    }

    [Fact]
    public void BlfFormat_ObjSignatureIsLobj()
    {
        BlfFormat.ObjSignature.Should().Be("LOBJ",
            "vblf_constants.py line 5: OBJ_SIGNATURE: Final = b\"LOBJ\"");
    }

    [Fact]
    public void BlfFormat_ObjTypeIds_ClassicCan()
    {
        // vblf_constants.py line 11: CAN_MESSAGE = 1
        // vblf_constants.py line 96: CAN_MESSAGE2 = 86
        BlfFormat.ObjTypeCanMessage.Should().Be(1u, "vblf CAN_MESSAGE=1");
        BlfFormat.ObjTypeCanMessage2.Should().Be(86u, "vblf CAN_MESSAGE2=86");
    }

    [Fact]
    public void BlfFormat_ObjTypeIds_CanFd()
    {
        // vblf_constants.py line 110-111: CAN_FD_MESSAGE=100, CAN_FD_MESSAGE_64=101
        BlfFormat.ObjTypeCanFdMessage.Should().Be(100u, "vblf CAN_FD_MESSAGE=100");
        BlfFormat.ObjTypeCanFdMessage64.Should().Be(101u, "vblf CAN_FD_MESSAGE_64=101");
    }

    [Fact]
    public void BlfFormat_ObjTypeIds_LogContainer()
    {
        // vblf_constants.py line 20: LOG_CONTAINER = 10
        BlfFormat.ObjTypeLogContainer.Should().Be(10u, "vblf LOG_CONTAINER=10");
    }

    [Fact]
    public void BlfFormat_FrameDataSizes_CorrectPerVblfStructFormats()
    {
        // vblf_can.py line 14: CanMessage._FORMAT = struct.Struct("HBBI8s")       → 12 bytes
        // vblf_can.py line 80: CanMessage2._FORMAT = struct.Struct("HBBI8sIBBH") → 28 bytes
        // vblf_can.py line 168: CanFdMessage._FORMAT = struct.Struct("HBBIIBBBBI64sI") → 76 bytes
        // vblf_can.py line 272: CanFdMessage64._FORMAT = struct.Struct("BBBBIIIIIIIHBBI") → 48 bytes
        BlfFormat.CanMessageDataSize.Should().Be(12);
        BlfFormat.CanMessage2DataSize.Should().Be(28);
        BlfFormat.CanFdMessageDataSize.Should().Be(76);
        BlfFormat.CanFdMessage64DataSize.Should().Be(48);
    }

    [Fact]
    public void BlfFormat_FileHeaderSizeIs144()
    {
        // vblf_general.py FileStatistics._FORMAT = 4sIIBBBBQQII32xQ64s → 144 bytes.
        BlfFormat.FileHeaderSize.Should().Be(144);
    }

    [Fact]
    public void BlfFormat_TimestampScaleIs10Million()
    {
        // vblf stores timestamp as 10ns ticks since Vector epoch.
        BlfFormat.TimestampScale.Should().Be(10_000_000.0);
    }

    // v3.51.0: verify the fixture's internal object structure rather than only
    // asserting its total length. The fixture is a single object payload and
    // therefore has no FileStatistics prefix.
    [Fact]
    public void BlfFormat_VblfTestFixture_ParsesObjectStructure()
    {
        var path = System.IO.Path.GetFullPath(VblfFixturePath);
        File.Exists(path).Should().BeTrue($"vblf fixture must exist at {path}");
        var bytes = File.ReadAllBytes(path);

        bytes.Length.Should().Be(48);
        Encoding.ASCII.GetString(bytes, 0, 4).Should().Be("LOBJ");
        BitConverter.ToUInt16(bytes, 4).Should().Be(32,
            "the fixture's base-plus-versioned object header size is 32 bytes");
        BitConverter.ToUInt16(bytes, 6).Should().Be(1,
            "vblf ObjectHeaderBase.header_version is 1 in the fixture");
        BitConverter.ToUInt32(bytes, 8).Should().Be(48u,
            "object_size includes the 32-byte header and 16-byte CAN_MESSAGE payload");
        BitConverter.ToUInt32(bytes, 12).Should().Be(1u,
            "vblf ObjectHeaderBase.object_type is CAN_MESSAGE=1");
        BitConverter.ToUInt32(bytes, 16).Should().Be(2u,
            "the fixture's object flags are encoded at the upstream header offset");
        bytes.Length.Should().Be(BlfFormat.ObjectHeaderSize + 16,
            "32-byte ObjectHeader plus 16-byte CanMessage payload (HBBI8s) = 48 bytes");
    }
}
