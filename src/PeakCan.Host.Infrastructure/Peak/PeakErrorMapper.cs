using PeakCan.Host.Core;

namespace PeakCan.Host.Infrastructure.Peak;

/// <summary>
/// Maps a PEAK PCAN-Basic <c>TPCANStatus</c> (a <see cref="uint"/> bit pattern)
/// to a canonical <see cref="ErrorCode"/> + a human-readable message. The
/// mapping intentionally collapses similar hardware states (e.g. XMTFULL +
/// BUSOFF both surface as <see cref="ErrorCode.HardwareBusy"/>) so the UI
/// can render a small finite set of recovery hints.
/// <para>
/// Caller contract: <c>0</c> is <i>not</i> an error. Use <see cref="IsOk"/>
/// before calling <see cref="ToErrorCode"/> when the source might return
/// success. Mapping <c>0</c> through <see cref="ToErrorCode"/> is supported
/// (it yields <c>(ErrorCode.Unknown, "OK")</c>) but is wasteful.
/// </para>
/// <para>
/// Composite status: PEAK drivers may OR non-error flag bits
/// (<c>PCAN_ERROR_INITIALIZE</c> / <c>PCAN_ERROR_RESOURCE</c> /
/// <c>PCAN_ERROR_ILLCLIENT</c>) onto a status code. Both <see cref="IsOk"/>
/// and <see cref="ToErrorCode"/> strip the high-16 flag mask
/// (<see cref="FlagMask"/>) before evaluating, so a composite like
/// <c>0x40000040u</c> (BUSOFF | INITIALIZE) surfaces as BUSOFF and
/// <c>0x00010000u</c> (RESOURCE only) surfaces as OK. The actual flag bit
/// positions come from <c>PCANBasic.h</c>; <c>0xFFFF0000u</c> is a
/// conservative upper bound on the flag region.
/// </para>
/// </summary>
public static class PeakErrorMapper
{
    /// <summary>
    /// High-16 mask of PEAK composite status flag bits. The mapper strips these
    /// before comparing or switching, so callers can pass a raw <c>TPCANStatus</c>
    /// directly without first ANDing the flags off.
    /// </summary>
    private const uint FlagMask = 0xFFFF0000u;

    /// <summary>True iff <paramref name="raw"/>, with high-16 flag bits stripped, is PEAK's success sentinel.</summary>
    public static bool IsOk(uint raw) => (raw & ~FlagMask) == PeakError.OK;

    /// <summary>
    /// Translate <paramref name="raw"/> to <c>(code, message)</c>. High-16
    /// flag bits are stripped before the switch so a composite like
    /// <c>BUSOFF | INITIALIZE</c> maps to BUSOFF. Unknown statuses fall
    /// through to <c>(ErrorCode.Unknown, "Unknown PCAN status 0xXXXXXXXX")</c>
    /// so the UI can still display something.
    /// </summary>
    public static (ErrorCode Code, string Message) ToErrorCode(uint raw)
    {
        var baseError = raw & ~FlagMask;
        return baseError switch
        {
            PeakError.OK => (ErrorCode.Unknown, "OK"),
            PeakError.XMTFULL => (ErrorCode.HardwareBusy, "Transmit buffer full"),
            PeakError.OVERRUN => (ErrorCode.IoError, "Receive overrun"),
            PeakError.BUSLIGHT => (ErrorCode.IoError, "Bus light error"),
            PeakError.BUSHEAVY => (ErrorCode.IoError, "Bus heavy error"),
            PeakError.BUSOFF => (ErrorCode.HardwareBusy, "Bus-off state"),
            PeakError.NODRIVER => (ErrorCode.HardwareNotAvailable, "PCAN driver not loaded"),
            PeakError.ILLHW => (ErrorCode.HardwareNotAvailable, "Illegal hardware"),
            PeakError.REGTEST => (ErrorCode.HardwareNotAvailable, "Driver init failed self-test"),
            PeakError.PARAM => (ErrorCode.HardwareParameter, "Illegal parameter"),
            _ => (ErrorCode.Unknown, $"Unknown PCAN status 0x{raw:X8}"),
        };
    }
}
