using PeakCan.Host.Core;

namespace PeakCan.Host.Infrastructure.Peak;

/// <summary>
/// Maps a PEAK PCAN-Basic <c>TPCANStatus</c> (a <see cref="uint"/> bit pattern)
/// to a canonical <see cref="ErrorCode"/> + a human-readable message. The
/// mapping intentionally collapses similar hardware states (e.g. XMTFULL +
/// BUSOFF both surface as <see cref="ErrorCode.HardwareBusy"/>) so the UI
/// can render a small finite set of recovery hints.
/// </summary>
public static class PeakErrorMapper
{
    /// <summary>
    /// Translate <paramref name="raw"/> to <c>(code, message)</c>. Unknown
    /// statuses fall through to <c>(ErrorCode.Unknown, "Unknown PCAN status 0xXXXXXXXX")</c>
    /// so the UI can still display something.
    /// </summary>
    public static (ErrorCode Code, string Message) ToErrorCode(uint raw) => raw switch
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
