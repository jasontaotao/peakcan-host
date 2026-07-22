using FluentAssertions;
using PeakCan.Host.Core.Uds.FlashPipeline;
using Xunit;

namespace PeakCan.Host.Core.Tests.Uds.FlashPipeline;

/// <summary>
/// Phase 1 C4 Task 1.3: <see cref="FirmwareFileParser"/> turns a firmware
/// file into a <see cref="FirmwareImage"/>, the in-memory payload the
/// PipelineExecutor streams to the ECU via RequestDownload + TransferData.
/// Phase 1 supports only raw binary blobs (the firmware file IS the flash
/// payload); hex/s-record formats are Phase 1.1 onwards. The memory address
/// is supplied separately by <c>FlashProfile.MemoryAddress</c> (per the
/// design total案 scope decision 2026-07-22), so the parser ONLY owns
/// data + length, not addressing.
/// </summary>
public sealed class FirmwareFileParserTests
{
    private static readonly byte[] SampleBytes =
    {
        0xA1, 0xB2, 0xC3, 0xD4, 0xE5, 0xF6, 0x07, 0x18,
        0x29, 0x3A, 0x4B, 0x5C, 0x6D, 0x7E, 0x8F, 0x90,
    };

    [Fact]
    public void Parse_Returns_Image_With_Data_And_Length()
    {
        // The most basic contract: bytes in, same bytes out, length equal to byte count.
        // PipelineExecutor uses Length for RequestDownload(uint address, uint length) and
        // then slices Data into TransferData chunks sized by the ECU-reported block length.
        var image = FirmwareFileParser.Parse(SampleBytes);

        image.Data.Should().Equal(SampleBytes);
        image.Length.Should().Be((uint)SampleBytes.Length);
    }

    [Fact]
    public void Parse_Returns_Defensive_Copy_Of_Input()
    {
        // FirmwareImage.Data must be an independent copy: if a caller later mutates
        // the source array (or the image's data), the other side must not see it —
        // the ECU is about to receive this exact payload, so aliasing a caller buffer
        // that gets reused mid-flash would silently corrupt the transfer.
        var source = (byte[])SampleBytes.Clone();
        var image = FirmwareFileParser.Parse(source);

        // Mutate source AFTER parse — image must be unaffected.
        source[0] = 0xFF;
        source[1] = 0xFF;

        image.Data[0].Should().Be(0xA1, "the image must not alias the caller's buffer");
        image.Data[1].Should().Be(0xB2);
    }

    [Fact]
    public void Parse_Empty_Input_Throws()
    {
        // A zero-length firmware is never legitimate — RequestDownload(0x..., length=0)
        // would make the ECU enter a TransferData loop with zero work, and some ECU
        // implementations NRC the empty download outright. Refuse early at parse time.
        var act = () => FirmwareFileParser.Parse(Array.Empty<byte>());
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parse_Null_Input_Throws()
    {
        var act = () => FirmwareFileParser.Parse(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Large_But_Legal_Image_Parses()
    {
        // RequestDownload's length field is a uint32, so any image < UInt32.MaxValue
        // is a valid download target. 256 MiB is a realistic automotive firmware size
        // and well within the protocol's 4-byte length envelope, so it MUST parse.
        // (Images larger than UInt32.MaxValue cannot use the current 4-byte length
        // format at all — the guard fires inside RequestDownload's caller, not at
        // parse time; no 4-GiB fixture is constructible in a unit test, so that path
        // is covered by an integration check later, not here.)
        var large = new byte[256 * 1024 * 1024];
        large[0] = 0x42;

        var image = FirmwareFileParser.Parse(large);

        image.Length.Should().Be((uint)large.Length);
        image.Data[0].Should().Be(0x42);
    }
}
