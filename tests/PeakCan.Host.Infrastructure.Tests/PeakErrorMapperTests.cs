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
}
