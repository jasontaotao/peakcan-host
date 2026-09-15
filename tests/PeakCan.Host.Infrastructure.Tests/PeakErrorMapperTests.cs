using FluentAssertions;
using Peak.Can.Basic.BackwardCompatibility;
using PeakCan.HIL.Core;
using PeakCan.Host.Infrastructure.Peak;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests;

/// <summary>
/// Verifies that PEAK PCAN-Basic status codes map to canonical
/// <see cref="ErrorCode"/> values + human-readable messages.
/// <para>
/// v3.66.0: rebaselined against the actual <see cref="TPCANStatus"/> enum from
/// <c>Peak.PCANBasic.NET 5.x</c>. The previous tests asserted the host's own
/// hand-rolled (wrong) constants, so they locked in the bug rather than catching
/// it (e.g. 0x40 is QOVERRUN, not BUSOFF; 0x20 is QRCVEMPTY, not NODRIVER).
/// </para>
/// </summary>
public class PeakErrorMapperTests
{
    [Theory]
    [InlineData(TPCANStatus.PCAN_ERROR_OK, ErrorCode.Ok)]
    [InlineData(TPCANStatus.PCAN_ERROR_XMTFULL, ErrorCode.HardwareBusy)]
    [InlineData(TPCANStatus.PCAN_ERROR_OVERRUN, ErrorCode.IoError)]
    [InlineData(TPCANStatus.PCAN_ERROR_QOVERRUN, ErrorCode.IoError)]
    [InlineData(TPCANStatus.PCAN_ERROR_QXMTFULL, ErrorCode.HardwareBusy)]
    [InlineData(TPCANStatus.PCAN_ERROR_BUSLIGHT, ErrorCode.IoError)]
    [InlineData(TPCANStatus.PCAN_ERROR_BUSHEAVY, ErrorCode.IoError)]
    [InlineData(TPCANStatus.PCAN_ERROR_BUSOFF, ErrorCode.HardwareBusy)]
    [InlineData(TPCANStatus.PCAN_ERROR_BUSPASSIVE, ErrorCode.IoError)]
    [InlineData(TPCANStatus.PCAN_ERROR_REGTEST, ErrorCode.HardwareNotAvailable)]
    [InlineData(TPCANStatus.PCAN_ERROR_NODRIVER, ErrorCode.HardwareNotAvailable)]
    [InlineData(TPCANStatus.PCAN_ERROR_HWINUSE, ErrorCode.HardwareBusy)]
    [InlineData(TPCANStatus.PCAN_ERROR_NETINUSE, ErrorCode.HardwareBusy)]
    [InlineData(TPCANStatus.PCAN_ERROR_ILLHW, ErrorCode.HardwareNotAvailable)]
    [InlineData(TPCANStatus.PCAN_ERROR_ILLNET, ErrorCode.HardwareNotAvailable)]
    [InlineData(TPCANStatus.PCAN_ERROR_ILLCLIENT, ErrorCode.HardwareParameter)]
    [InlineData(TPCANStatus.PCAN_ERROR_RESOURCE, ErrorCode.HardwareNotAvailable)]
    [InlineData(TPCANStatus.PCAN_ERROR_ILLPARAMTYPE, ErrorCode.HardwareParameter)]
    [InlineData(TPCANStatus.PCAN_ERROR_ILLPARAMVAL, ErrorCode.HardwareParameter)]
    [InlineData(TPCANStatus.PCAN_ERROR_ILLDATA, ErrorCode.HardwareParameter)]
    [InlineData(TPCANStatus.PCAN_ERROR_ILLMODE, ErrorCode.HardwareParameter)]
    [InlineData(TPCANStatus.PCAN_ERROR_ILLOPERATION, ErrorCode.HardwareParameter)]
    public void Maps_Known_PCAN_Status_To_ErrorCode(TPCANStatus raw, ErrorCode expected)
    {
        var (code, _) = PeakErrorMapper.ToErrorCode((uint)raw);
        code.Should().Be(expected);
    }

    /// <summary>
    /// F1-2 regression: the old mapper stripped everything &gt;= 0x10000 as an
    /// "advisory flag", so these genuine errors collapsed to (Ok, "OK"). A failed
    /// InitializeFD/Write would then be treated as success by the connect/write
    /// paths.
    /// </summary>
    [Theory]
    [InlineData(TPCANStatus.PCAN_ERROR_UNKNOWN)]      // 0x0010000
    [InlineData(TPCANStatus.PCAN_ERROR_ILLDATA)]      // 0x0020000
    [InlineData(TPCANStatus.PCAN_ERROR_BUSPASSIVE)]   // 0x0040000
    [InlineData(TPCANStatus.PCAN_ERROR_ILLMODE)]      // 0x0080000
    [InlineData(TPCANStatus.PCAN_ERROR_ILLOPERATION)] // 0x8000000
    [InlineData(TPCANStatus.PCAN_ERROR_RESOURCE)]     // 0x0002000
    [InlineData(TPCANStatus.PCAN_ERROR_ILLPARAMVAL)]  // 0x0008000
    public void Real_Errors_Are_Not_Reported_As_Ok(TPCANStatus raw)
    {
        PeakErrorMapper.IsOk((uint)raw).Should().BeFalse(
            $"0x{(uint)raw:X8} is a genuine error, not an advisory flag");
        PeakErrorMapper.ToErrorCode((uint)raw).Code.Should().NotBe(ErrorCode.Ok);
    }

    [Fact]
    public void Ok_Status_Returns_Ok_Message()
    {
        var (_, message) = PeakErrorMapper.ToErrorCode((uint)TPCANStatus.PCAN_ERROR_OK);
        message.Should().Be("OK");
    }

    [Fact]
    public void Unknown_Status_Falls_Through_With_Hex_Message()
    {
        // 0x00100000 is not a named TPCANStatus value and carries no bus-error bits.
        var (code, message) = PeakErrorMapper.ToErrorCode(0x00100000u);
        code.Should().Be(ErrorCode.Unknown);
        message.Should().Be("Unknown PCAN status 0x00100000");
    }

    [Fact]
    public void Known_Status_Message_Is_Not_Empty()
    {
        // Every mapped status must produce a non-empty human-readable string
        // for the UI; this guards against future additions that forget the
        // message half of the tuple.
        foreach (TPCANStatus raw in Enum.GetValues<TPCANStatus>())
        {
            var (_, message) = PeakErrorMapper.ToErrorCode((uint)raw);
            message.Should().NotBeNullOrWhiteSpace($"status 0x{(uint)raw:X8} must have a message");
        }
    }

    [Theory]
    [InlineData((uint)TPCANStatus.PCAN_ERROR_OK, true)]
    [InlineData((uint)TPCANStatus.PCAN_ERROR_QRCVEMPTY, true)]   // benign: no message available
    [InlineData((uint)TPCANStatus.PCAN_ERROR_XMTFULL, false)]
    [InlineData((uint)TPCANStatus.PCAN_ERROR_BUSOFF, false)]
    [InlineData(0xDEADBEEFu, false)]
    [InlineData(uint.MaxValue, false)]
    public void IsOk_Detects_Success_Sentinel(uint raw, bool expected)
    {
        PeakErrorMapper.IsOk(raw).Should().Be(expected);
    }

    [Fact]
    public void Caution_Is_Advisory_On_Success()
    {
        // PCAN_ERROR_CAUTION = "operation was carried out, however irregularities
        // were registered" → still a success.
        PeakErrorMapper.IsOk((uint)TPCANStatus.PCAN_ERROR_CAUTION).Should().BeTrue();
        var (code, message) = PeakErrorMapper.ToErrorCode((uint)TPCANStatus.PCAN_ERROR_CAUTION);
        code.Should().Be(ErrorCode.Ok);
        message.Should().Be("OK");
    }

    [Fact]
    public void Initialize_Alone_Is_A_Failure()
    {
        // "The PCAN Channel is not (or could not be) initialized." — the old
        // mapper treated this as OK (masked to base 0), which could let a connect
        // proceed against an uninitialized channel.
        PeakErrorMapper.IsOk((uint)TPCANStatus.PCAN_ERROR_INITIALIZE).Should().BeFalse();
        var (code, message) = PeakErrorMapper.ToErrorCode((uint)TPCANStatus.PCAN_ERROR_INITIALIZE);
        code.Should().Be(ErrorCode.HardwareNotAvailable);
        message.Should().Be("PCAN channel not initialized");
    }

    [Fact]
    public void Initialize_Combined_With_Concrete_Error_Surfaces_The_Concrete_Error()
    {
        // 0x4000010 = INITIALIZE | BUSOFF → the bus-off state must surface.
        var raw = (uint)(TPCANStatus.PCAN_ERROR_INITIALIZE | TPCANStatus.PCAN_ERROR_BUSOFF);
        PeakErrorMapper.IsOk(raw).Should().BeFalse();
        var (code, message) = PeakErrorMapper.ToErrorCode(raw);
        code.Should().Be(ErrorCode.HardwareBusy);
        message.Should().Be("Bus-off state");
    }

    [Fact]
    public void Composite_Bus_Error_Mask_Prefers_BusOff()
    {
        // PCAN_ERROR_ANYBUSERR = BUSPASSIVE | BUSOFF | BUSHEAVY | BUSLIGHT.
        var (code, message) = PeakErrorMapper.ToErrorCode((uint)TPCANStatus.PCAN_ERROR_ANYBUSERR);
        code.Should().Be(ErrorCode.HardwareBusy);
        message.Should().Be("Bus-off state");
    }

    [Fact]
    public void Unnamed_High_Bit_Falls_Through_To_Unknown()
    {
        // 0x80000000 is not a named TPCANStatus value and carries no bus-error bits.
        var (code, message) = PeakErrorMapper.ToErrorCode(0x80000000u);
        code.Should().Be(ErrorCode.Unknown);
        message.Should().Be("Unknown PCAN status 0x80000000");
    }
}
