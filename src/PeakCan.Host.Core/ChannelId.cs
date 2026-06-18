namespace PeakCan.Host.Core;

/// <summary>
/// Stable identifier for a PCAN-Basic channel handle (e.g. <c>PCAN_USBBUS1</c>).
/// Wraps the underlying <c>ushort</c> handle so domain code stays type-safe.
/// </summary>
public readonly record struct ChannelId(ushort Handle)
{
    /// <summary>Sentinel for "no channel assigned" (e.g. synthesized frames in tests).</summary>
    public static ChannelId None => default;

    public override string ToString() => $"ch{Handle}";
}