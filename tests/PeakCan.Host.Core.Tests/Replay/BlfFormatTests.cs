using FluentAssertions;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Replay;

/// <summary>
/// v3.51.0 MINOR: verifies BlfFormat constants match the Vector BLF spec.
/// Sister of SignalDecoderTests / AscFormatRoundTripTests (v3.49.0).
/// </summary>
public class BlfFormatTests
{
    [Fact]
    public void BlfFormat_HasExpectedFileSignature()
    {
        BlfFormat.FileSignature.Should().Be("LOGG",
            "BLF file header starts with 'LOGG' per Vector spec");
    }

    [Fact]
    public void BlfFormat_HasExpectedFormatSignature()
    {
        BlfFormat.FormatSignature.Should().Be("LBLF",
            "BLF format magic 'LBLF' follows 'LOGG' per Vector spec");
    }

    [Fact]
    public void BlfFormat_HasExpectedObjectSignatures()
    {
        BlfFormat.ObjHeader.Should().Be("OBJH");
        BlfFormat.Blob.Should().Be("BLOB");
        BlfFormat.Container.Should().Be("LOBJ");
    }

    [Fact]
    public void BlfFormat_HasExpectedFrameContainerTypeIds()
    {
        // Per Vector BLF spec — these IDs identify classic CAN, CAN FD,
        // and CAN XL frame types inside an OBJH-typed BLOB.
        BlfFormat.ET_CAN_DATA.Should().Be(5u, "classic CAN BLOB type id per Vector spec");
        BlfFormat.ET_CAN_FD_DATA.Should().Be(29u, "CAN FD BLOB type id per Vector spec");
        BlfFormat.ET_CAN_XL_DATA.Should().Be(33u, "CAN XL BLOB type id per Vector spec");
    }

    [Fact]
    public void BlfFormat_HasExpectedBlobSizes()
    {
        BlfFormat.ClassicCanBlobSize.Should().Be(20,
            "classic CAN BLOB = 20 bytes per Vector spec");
        BlfFormat.CanFdBlobSize.Should().Be(32,
            "CAN FD BLOB = 32 bytes (header) + variable data per Vector spec");
        BlfFormat.CanXlBlobMinSize.Should().Be(32,
            "CAN XL BLOB = 32 bytes (header) + variable data per Vector spec");
    }

    [Fact]
    public void BlfFormat_HasExpectedTimestampScale()
    {
        BlfFormat.TimestampScale.Should().Be(10_000_000.0,
            "OBJH timestamp is UINT64 in 100ns ticks; 10_000_000 ticks = 1 second");
    }
}