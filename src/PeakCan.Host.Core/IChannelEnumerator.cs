namespace PeakCan.Host.Core;

/// <summary>
/// Enumerates available CAN channels on the system. The MVP probes
/// a fixed set of PEAK USB handles; a future version can enumerate
/// via the PEAK API or support other hardware backends.
/// </summary>
public interface IChannelEnumerator
{
    /// <summary>
    /// Probe all known channel handles and return those that responded.
    /// Non-throwing: if no hardware is detected, returns an empty list.
    /// </summary>
    IReadOnlyList<ChannelInfo> Enumerate();
}

/// <summary>
/// One detected CAN channel. <see cref="Handle"/> is the raw
/// PEAK handle (e.g. 0x51 for PCAN-USB FD channel 1);
/// <see cref="Name"/> is a human-readable label for the UI.
/// </summary>
public sealed record ChannelInfo(ushort Handle, string Name);
