namespace PeakCan.Host.Core.Replay;

/// <summary>
/// One CAN frame parsed from an ASC trace file.
/// Immutable. <see cref="Timestamp"/> is seconds from recording start.
/// </summary>
public sealed record ReplayFrame(
    double Timestamp,
    uint Id,
    byte Dlc,
    byte[] Data,
    FrameFlags Flags);