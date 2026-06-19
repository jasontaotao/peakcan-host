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
/// Out of scope for MVP: the mapper does not strip the bit-flags
/// <c>PCAN_ERROR_INITIALIZE</c> / <c>PCAN_ERROR_RESOURCE</c> /
/// <c>PCAN_ERROR_ILLCLIENT</c> that some PEAK drivers OR onto a status code.
/// A composite like <c>0x40000040u</c> (BUSOFF | INITIALIZE) falls through
/// to the unknown arm. Follow-up if/when callers actually surface these
/// composite codes — strip the flag bits, then map the remainder.
/// </para>
/// </summary>
public static class PeakErrorMapper
{
    /// <summary>True iff <paramref name="raw"/> is PEAK's success sentinel.</summary>
    public static bool IsOk(uint raw) => raw == PeakError.OK;

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
