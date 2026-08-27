namespace PeakCan.HIL.Core.Replay;

/// <summary>
/// One CAN frame parsed from an ASC or BLF trace file.
/// Immutable. <see cref="Timestamp"/> is seconds from recording start.
/// <para>
/// <see cref="Id"/> is always the <b>raw CAN identifier with no format marker
/// bit</b>: BLF parser masks off bit 31 (Vector's extended-format marker) and
/// reflects the format in <see cref="IsExtended"/>; ASC parser stores the bare
/// hex value. Consumers must never re-derive the format from <see cref="Id"/>'s
/// bit pattern — read <see cref="IsExtended"/> instead.
/// </para>
/// </summary>
public sealed record ReplayFrame(
    double Timestamp,
    uint Id,
    byte Dlc,
    byte[] Data,
    FrameFlags Flags,
    bool IsExtended = false);
