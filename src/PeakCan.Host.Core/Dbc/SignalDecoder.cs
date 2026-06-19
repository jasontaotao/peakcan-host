namespace PeakCan.Host.Core.Dbc;

/// <summary>
/// Decodes a raw CAN frame payload into the engineering value of one
/// <see cref="Signal"/>, applying the DBC bit-numbering convention for the
/// signal's <see cref="ByteOrder"/>, sign extension (for <see cref="ValueType.Signed"/>),
/// IEEE 754 reinterpretation (for <see cref="ValueType.Float"/> / <see cref="ValueType.Double"/>),
/// and the engineering <c>factor</c> + <c>offset</c>.
/// </summary>
public static class SignalDecoder
{
    /// <summary>
    /// Decode <paramref name="signal"/> from <paramref name="data"/>.
    /// </summary>
    /// <param name="data">Frame payload bytes (length 0..64 for classic CAN, up to 64 for CAN FD).</param>
    /// <param name="signal">Signal definition extracted from the DBC.</param>
    /// <returns>Engineering value: <c>raw_physical * factor + offset</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="signal"/>.Length &gt; 64 (outside the MVP scope).
    /// </exception>
    public static double Decode(ReadOnlySpan<byte> data, Signal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (signal.Length == 0) return 0.0;
        if (signal.Length > 64)
        {
            throw new ArgumentOutOfRangeException(
                nameof(signal), signal.Length,
                "Signal > 64 bits not supported in MVP (CAN FD max payload is 64 bytes).");
        }

        ulong raw = signal.Order == ByteOrder.LittleEndian
            ? ReadLittleEndian(data, signal.StartBit, signal.Length)
            : ReadBigEndian(data, signal.StartBit, signal.Length);

        double physical = signal.ValueType switch
        {
            ValueType.Unsigned => raw,
            ValueType.Signed => SignExtend(raw, signal.Length),
            ValueType.Float => BitConverter.Int32BitsToSingle((int)raw),
            ValueType.Double => BitConverter.Int64BitsToDouble((long)raw),
            _ => raw,
        };
        return physical * signal.Factor + signal.Offset;
    }

    // DBC little-endian (Intel): start bit is the LSB of the first byte;
    // the field grows toward higher byte indices.
    private static ulong ReadLittleEndian(ReadOnlySpan<byte> data, byte start, byte len)
    {
        ulong result = 0;
        for (byte i = 0; i < len; i++)
        {
            byte byteIdx = (byte)((start + i) / 8);
            byte bitIdx = (byte)((start + i) % 8);
            if (byteIdx >= data.Length) break;          // not enough data — treat remaining as 0
            ulong bit = (ulong)(data[byteIdx] >> bitIdx) & 1UL;
            result |= bit << i;
        }
        return result;
    }

    // DBC big-endian (Motorola): bit 0 is the MSB of byte 0, grows toward
    // byte 1 etc.
    private static ulong ReadBigEndian(ReadOnlySpan<byte> data, byte start, byte len)
    {
        ulong result = 0;
        for (byte i = 0; i < len; i++)
        {
            byte absBit = (byte)(start + i);
            byte byteIdx = (byte)(absBit / 8);
            byte bitInByte = (byte)(7 - (absBit % 8));  // MSB-first within the byte
            if (byteIdx >= data.Length) break;
            ulong bit = (ulong)(data[byteIdx] >> bitInByte) & 1UL;
            result = (result << 1) | bit;
        }
        return result;
    }

    private static long SignExtend(ulong raw, byte len)
    {
        if (len >= 64) return (long)raw;
        ulong sign = 1UL << (len - 1);
        if ((raw & sign) == 0) return (long)raw;
        ulong mask = ~((1UL << len) - 1);
        return (long)(raw | mask);
    }
}
