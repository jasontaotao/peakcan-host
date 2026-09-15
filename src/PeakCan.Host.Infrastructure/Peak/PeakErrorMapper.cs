using Peak.Can.Basic.BackwardCompatibility;
using PeakCan.HIL.Core;

namespace PeakCan.Host.Infrastructure.Peak;

/// <summary>
/// Maps a PEAK PCAN-Basic <see cref="TPCANStatus"/> to a canonical
/// <see cref="ErrorCode"/> + a human-readable message. The mapping intentionally
/// collapses similar hardware states (e.g. XMTFULL + QXMTFULL both surface as
/// <see cref="ErrorCode.HardwareBusy"/>) so the UI can render a small finite set
/// of recovery hints.
/// <para>
/// v3.66.0 FIX (F1-2): the previous implementation hand-rolled the status
/// constants with wrong values and treated every bit &gt;= 0x10000 as an
/// "advisory flag" to be masked off. That made genuine errors report as success
/// (<c>UNKNOWN 0x10000</c>, <c>ILLDATA 0x20000</c>, <c>ILLMODE 0x80000</c>,
/// <c>ILLOPERATION 0x8000000</c>, <c>RESOURCE 0x2000</c>, ...) and mis-mapped
/// others (0x20 <c>QRCVEMPTY</c> reported as "driver not loaded", 0x40
/// <c>QOVERRUN</c> reported as "bus-off"). The mapper now switches on the SDK
/// enum itself (<c>Peak.PCANBasic.NET 5.x</c>) instead of magic numbers.
/// </para>
/// <para>
/// Only <see cref="TPCANStatus.PCAN_ERROR_CAUTION"/> is a pure advisory bit
/// (documented as "an operation was successfully carried out, however,
/// irregularities were registered"). <see cref="TPCANStatus.PCAN_ERROR_INITIALIZE"/>
/// is stripped only when a concrete error rides along with it, so the concrete
/// error surfaces; a standalone INITIALIZE is reported as a failure.
/// </para>
/// </summary>
public static class PeakErrorMapper
{
    /// <summary>Advisory bit that annotates a <em>successful</em> call (never masks a failure).</summary>
    private const TPCANStatus AdvisoryFlags = TPCANStatus.PCAN_ERROR_CAUTION;

    /// <summary>Benign "no message available" status returned by a successful empty read.</summary>
    private const TPCANStatus NoDataStatus = TPCANStatus.PCAN_ERROR_QRCVEMPTY;

    /// <summary>
    /// True iff <paramref name="raw"/> reports success: the OK sentinel, an
    /// OK status annotated with the advisory CAUTION bit, or the benign
    /// "receive queue empty" status.
    /// </summary>
    public static bool IsOk(uint raw)
    {
        var status = (TPCANStatus)raw & ~AdvisoryFlags;
        return status == TPCANStatus.PCAN_ERROR_OK || status == NoDataStatus;
    }

    /// <summary>
    /// Translate <paramref name="raw"/> to <c>(code, message)</c>. Unknown
    /// statuses fall through to <c>(ErrorCode.Unknown, "Unknown PCAN status
    /// 0xXXXXXXXX")</c> so the UI can still display something.
    /// </summary>
    public static (ErrorCode Code, string Message) ToErrorCode(uint raw)
    {
        var status = (TPCANStatus)raw;

        // Strip the advisory CAUTION bit; strip INITIALIZE only when a concrete
        // error rides along with it (PEAK ORs INITIALIZE onto other codes when
        // the channel was never initialized) so the concrete error is surfaced.
        var concrete = status & ~(AdvisoryFlags | TPCANStatus.PCAN_ERROR_INITIALIZE);
        if (concrete == TPCANStatus.PCAN_ERROR_OK)
        {
            if ((status & TPCANStatus.PCAN_ERROR_INITIALIZE) != 0)
                return (ErrorCode.HardwareNotAvailable, "PCAN channel not initialized");
            return (ErrorCode.Ok, "OK"); // OK, or OK | CAUTION
        }

        return concrete switch
        {
            TPCANStatus.PCAN_ERROR_XMTFULL => (ErrorCode.HardwareBusy, "Transmit buffer full"),
            TPCANStatus.PCAN_ERROR_OVERRUN => (ErrorCode.IoError, "Receive overrun"),
            TPCANStatus.PCAN_ERROR_QOVERRUN => (ErrorCode.IoError, "Transmit queue overrun"),
            TPCANStatus.PCAN_ERROR_QXMTFULL => (ErrorCode.HardwareBusy, "Transmit queue full"),
            TPCANStatus.PCAN_ERROR_QRCVEMPTY => (ErrorCode.Ok, "Receive queue empty"),
            TPCANStatus.PCAN_ERROR_BUSLIGHT => (ErrorCode.IoError, "Bus light error"),
            TPCANStatus.PCAN_ERROR_BUSHEAVY => (ErrorCode.IoError, "Bus heavy error"),
            TPCANStatus.PCAN_ERROR_BUSOFF => (ErrorCode.HardwareBusy, "Bus-off state"),
            TPCANStatus.PCAN_ERROR_BUSPASSIVE => (ErrorCode.IoError, "Bus passive (error-passive)"),
            TPCANStatus.PCAN_ERROR_REGTEST => (ErrorCode.HardwareNotAvailable, "Driver init failed self-test"),
            TPCANStatus.PCAN_ERROR_NODRIVER => (ErrorCode.HardwareNotAvailable, "PCAN driver not loaded"),
            TPCANStatus.PCAN_ERROR_HWINUSE => (ErrorCode.HardwareBusy, "Hardware in use by another client"),
            TPCANStatus.PCAN_ERROR_NETINUSE => (ErrorCode.HardwareBusy, "Network in use by another client"),
            TPCANStatus.PCAN_ERROR_ILLHW => (ErrorCode.HardwareNotAvailable, "Illegal hardware"),
            TPCANStatus.PCAN_ERROR_ILLNET => (ErrorCode.HardwareNotAvailable, "Illegal network"),
            // ILLCLIENT and ILLHANDLE share 0x1C00 — one arm covers both.
            TPCANStatus.PCAN_ERROR_ILLCLIENT => (ErrorCode.HardwareParameter, "Illegal client handle"),
            TPCANStatus.PCAN_ERROR_RESOURCE => (ErrorCode.HardwareNotAvailable, "Required resource in use"),
            TPCANStatus.PCAN_ERROR_ILLPARAMTYPE => (ErrorCode.HardwareParameter, "Illegal parameter type"),
            TPCANStatus.PCAN_ERROR_ILLPARAMVAL => (ErrorCode.HardwareParameter, "Illegal parameter value"),
            TPCANStatus.PCAN_ERROR_ILLDATA => (ErrorCode.HardwareParameter, "Illegal data"),
            TPCANStatus.PCAN_ERROR_ILLMODE => (ErrorCode.HardwareParameter, "Illegal mode"),
            TPCANStatus.PCAN_ERROR_ILLOPERATION => (ErrorCode.HardwareParameter, "Illegal operation"),
            _ => ClassifyCompositeOrUnknown(concrete, raw),
        };
    }

    /// <summary>
    /// Last-resort classification for composite bus-error masks (e.g.
    /// <c>PCAN_ERROR_ANYBUSERR</c> = BUSPASSIVE|BUSOFF|BUSHEAVY|BUSLIGHT) that
    /// do not match a single arm above. Prefers the most severe bus state.
    /// </summary>
    private static (ErrorCode Code, string Message) ClassifyCompositeOrUnknown(TPCANStatus status, uint raw)
    {
        if ((status & TPCANStatus.PCAN_ERROR_BUSOFF) != 0) return (ErrorCode.HardwareBusy, "Bus-off state");
        if ((status & TPCANStatus.PCAN_ERROR_BUSPASSIVE) != 0) return (ErrorCode.IoError, "Bus passive (error-passive)");
        if ((status & TPCANStatus.PCAN_ERROR_BUSHEAVY) != 0) return (ErrorCode.IoError, "Bus heavy error");
        if ((status & TPCANStatus.PCAN_ERROR_BUSLIGHT) != 0) return (ErrorCode.IoError, "Bus light error");
        if ((status & TPCANStatus.PCAN_ERROR_UNKNOWN) != 0) return (ErrorCode.Unknown, "PEAK driver reported an unknown error");
        return (ErrorCode.Unknown, $"Unknown PCAN status 0x{raw:X8}");
    }
}
