namespace PeakCan.Host.Infrastructure.Channel;

/// <summary>
/// Pure helpers for the <c>PeakCanChannel</c> adapter. Extracted from the
/// wrapper class so they can be unit-tested without the PEAK SDK.
/// </summary>
internal static class PeakCanFrameFormatter
{
    /// <summary>
    /// Translate a CAN-FD DLC code (0..15) to its actual byte count on the wire.
    /// DLC 0..8 are literal; 9..15 follow the CAN-FD size table.
    /// </summary>
    public static byte DlcToBytes(byte dlc) => dlc switch
    {
        <= 8 => dlc,
        9 => 12,
        10 => 16,
        11 => 20,
        12 => 24,
        13 => 32,
        14 => 48,
        _ => 64,
    };

    /// <summary>
    /// Copy up to 8 bytes from <paramref name="src"/> into a fresh 8-byte
    /// array, padding with zeros if the source is shorter (or silently
    /// dropping the tail if it is longer — classic CAN DLC is capped at 8
    /// so this only happens for malformed input).
    /// </summary>
    public static byte[] ToFixedBytes8(ReadOnlyMemory<byte> src)
    {
        var dst = new byte[8];
        var len = Math.Min(src.Length, dst.Length);
        if (len > 0) Buffer.BlockCopy(src.ToArray(), 0, dst, 0, len);
        return dst;
    }

    /// <summary>
    /// Copy up to 64 bytes from <paramref name="src"/> into a fresh 64-byte
    /// array, padding with zeros if the source is shorter (or silently
    /// dropping the tail if it is longer — CAN-FD DLC 15 is the max so this
    /// only happens for malformed input).
    /// </summary>
    public static byte[] ToFixedBytes64(ReadOnlyMemory<byte> src)
    {
        var dst = new byte[64];
        var len = Math.Min(src.Length, dst.Length);
        if (len > 0) Buffer.BlockCopy(src.ToArray(), 0, dst, 0, len);
        return dst;
    }
}
