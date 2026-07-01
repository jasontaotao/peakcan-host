namespace PeakCan.Host.Core;

/// <summary>
/// Bit flags describing frame-level properties (RTR, CAN FD BRS/ESI, error frame).
/// Mirrors the relevant bits of <c>TPCANMsg.FRAME_TYPE</c> + extra hardware flags.
/// </summary>
[Flags]
public enum FrameFlags : ushort
{
    None = 0,

    /// <summary>CAN 2.0 Remote Transmission Request (RTR).</summary>
    Rtr = 1 << 0,

    /// <summary>CAN FD Bit Rate Switch (BRS) — data phase runs at higher baud.</summary>
    BitRateSwitch = 1 << 1,

    /// <summary>CAN FD Error State Indicator (ESI) — transmitter is error passive.</summary>
    ErrorStateIndicator = 1 << 2,

    /// <summary>PCAN error frame (bus error / acknowledge / form / bit-stuffing …).</summary>
    ErrFrame = 1 << 3,

    /// <summary>CAN FD format (DLC up to 64 bytes).</summary>
    Fd = 1 << 4,
}