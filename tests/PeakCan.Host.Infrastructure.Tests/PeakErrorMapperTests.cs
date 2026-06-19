using FluentAssertions;
using PeakCan.Host.Core;
using PeakCan.Host.Infrastructure.Peak;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests;

/// <summary>
/// Task 8: verifies that PEAK PCAN-Basic status codes map to canonical
/// <see cref="ErrorCode"/> values + human-readable messages. The mapper is
/// pure (uint in, tuple out) and safe to call from any thread.
/// </summary>
public class PeakErrorMapperTests
{
    [Theory]
    [InlineData(0x00000000u, ErrorCode.Unknown)]                  // PCAN_ERROR_OK
    [InlineData(0x00000001u, ErrorCode.HardwareBusy)]            // PCAN_ERROR_XMTFULL
    [InlineData(0x00000009u, ErrorCode.HardwareNotAvailable)]    // PCAN_ERROR_ILLHW — "illegal hardware" = not available
    [InlineData(0x00000020u, ErrorCode.HardwareNotAvailable)]    // PCAN_ERROR_NODRIVER
    [InlineData(0x00000040u, ErrorCode.HardwareBusy)]            // PCAN_ERROR_BUSOFF
    public void Maps_Known_PCAN_Status_To_ErrorCode(uint raw, ErrorCode expected)
    {
        var (code, _) = PeakErrorMapper.ToErrorCode(raw);
        code.Should().Be(expected);
    }

    [Fact]
    public void Ok_Status_Returns_Ok_Message()
    {
        var (_, message) = PeakErrorMapper.ToErrorCode(PeakError.OK);
        message.Should().Be("OK");
    }

    [Fact]
    public void Unknown_Status_Falls_Through_With_Hex_Message()
    {
        var (code, message) = PeakErrorMapper.ToErrorCode(0xDEADBEEFu);
        code.Should().Be(ErrorCode.Unknown);
        message.Should().Be("Unknown PCAN status 0xDEADBEEF");
    }

    [Fact]
    public void Known_Status_Message_Is_Not_Empty()
    {
        // Every mapped status must produce a non-empty human-readable string
        // for the UI; this guards against future additions that forget the
        // message half of the tuple.
        foreach (uint raw in new[] {
            PeakError.OK, PeakError.XMTFULL, PeakError.OVERRUN,
            PeakError.BUSLIGHT, PeakError.BUSHEAVY, PeakError.BUSOFF,
            PeakError.NODRIVER, PeakError.ILLHW, PeakError.REGTEST, PeakError.PARAM
        })
        {
            var (_, message) = PeakErrorMapper.ToErrorCode(raw);
            message.Should().NotBeNullOrWhiteSpace($"status 0x{raw:X8} must have a message");
        }
    }

    [Theory]
    [InlineData(PeakError.OK, true)]
    [InlineData(PeakError.XMTFULL, false)]
    [InlineData(0xDEADBEEFu, false)]
    [InlineData(uint.MaxValue, false)]
    public void IsOk_Detects_Success_Sentinel(uint raw, bool expected)
    {
        PeakErrorMapper.IsOk(raw).Should().Be(expected);
    }

    [Fact]
    public void Bitmasked_Composite_Code_Falls_Through_To_Unknown()
    {
        // 0x40000040u = BUSOFF | PCAN_ERROR_INITIALIZE — a composite that some
        // PEAK drivers return. The MVP mapper does not strip the flag bits,
        // so this must surface as Unknown with a hex-formatted message.
        var (code, message) = PeakErrorMapper.ToErrorCode(0x40000040u);
        code.Should().Be(ErrorCode.Unknown);
        message.Should().Be("Unknown PCAN status 0x40000040");
    }

    [Fact]
    public void Max_Uint_Falls_Through_To_Unknown()
    {
        var (code, message) = PeakErrorMapper.ToErrorCode(uint.MaxValue);
        code.Should().Be(ErrorCode.Unknown);
        message.Should().Be("Unknown PCAN status 0xFFFFFFFF");
    }
}
