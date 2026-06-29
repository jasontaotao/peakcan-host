namespace PeakCan.Host.Core.Dbc;

/// <summary>
/// Encodes signal engineering values into CAN frame bytes — the inverse
/// of <see cref="SignalDecoder.Decode"/>. DI singleton, stateless.
/// Handles DBC endianness (Little/Big), sign extension (Unsigned/Signed),
/// IEEE-754 reinterpretation (Float/Double), and engineering scale/offset.
/// </summary>
public sealed class DbcEncodeService
{
    /// <summary>
    /// Encode <paramref name="signalValues"/> into a fresh <c>Dlc</c>-sized byte
    /// array for the given <paramref name="message"/>.
    /// </summary>
    /// <exception cref="DbcSignalValueOutOfRangeException">
    /// Physical value outside [Min, Max] for the signal.
    /// </exception>
    /// <exception cref="DbcSignalNotFoundException">
    /// Signal name in <paramref name="signalValues"/> not present in message.
    /// </exception>
    /// <exception cref="DbcMultiplexorRequiredException">
    /// Multiplexed message missing multiplexor value in input.
    /// </exception>
    public byte[] Encode(Message message, IReadOnlyDictionary<string, double> signalValues)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(signalValues);

        // Build a set of valid signal names for fast lookup + validation.
        var validNames = new HashSet<string>(message.Signals.Count, StringComparer.Ordinal);
        foreach (var s in message.Signals) validNames.Add(s.Name);

        // Reject input keys that are not defined in the message.
        foreach (var key in signalValues.Keys)
        {
            if (!validNames.Contains(key))
            {
                throw new DbcSignalNotFoundException(message.Name, key);
            }
        }

        // Resolve multiplexor value first (if message is multiplexed)
        ushort? muxValue = null;
        if (message.IsMultiplexed && message.MultiplexorSignalIndex is { } muxIdx)
        {
            var muxSignal = message.Signals[muxIdx];
            if (!signalValues.TryGetValue(muxSignal.Name, out var muxDouble))
            {
                throw new DbcMultiplexorRequiredException(message.Name);
            }
            var muxRaw = PhysicalToRawBits(muxDouble, muxSignal);
            if (muxRaw < 0 || muxRaw > ushort.MaxValue)
            {
                throw new DbcSignalValueOutOfRangeException(message.Name, muxSignal.Name, muxDouble, muxSignal.Min, muxSignal.Max);
            }
            muxValue = (ushort)muxRaw;
        }

        var bytes = new byte[message.Dlc];

        foreach (var signal in message.Signals)
        {
            // Skip signals gated by a different mux value
            if (signal.IsMultiplexed && signal.MultiplexValue is ushort sigMux && muxValue is ushort curMux && sigMux != curMux)
                continue;

            if (!signalValues.TryGetValue(signal.Name, out var physical))
                continue;  // signal not in input → leave bits as 0

            // Validate range
            if (physical < signal.Min || physical > signal.Max)
            {
                throw new DbcSignalValueOutOfRangeException(message.Name, signal.Name, physical, signal.Min, signal.Max);
            }

            var raw = PhysicalToRawBits(physical, signal);
            WriteSignal(bytes, signal, raw);
        }

        return bytes;
    }

    // Compute the unsigned bit pattern that, when read back by
    // SignalDecoder.Decode, yields the given physical value. The decoder's
    // arithmetic is `IEEE_or_int(raw) * factor + offset`, so we invert it:
    //   raw_pre = (physical - offset) / factor
    // For Unsigned: the bit pattern is the integer value.
    // For Signed: the bit pattern is the two's-complement representation
    //   truncated to `length` bits (e.g. 8-bit signed -1 → 0xFF).
    // For Float / Double: the bit pattern is the IEEE-754 representation
    //   of the (single / double) raw_pre value.
    private static ulong PhysicalToRawBits(double physical, Signal signal)
    {
        if (signal.Factor == 0)
        {
            throw new DbcSignalConfigurationException(string.Empty, signal.Name, "Factor=0 (divide by zero)");
        }
        var raw = (physical - signal.Offset) / signal.Factor;

        return signal.ValueType switch
        {
            ValueType.Unsigned => (ulong)Math.Round(raw, MidpointRounding.AwayFromZero),
            // For signed, the bit pattern is the two's-complement
            // representation truncated to `length` bits (e.g. 8-bit signed -1
            // → 0xFF). (ulong)(double) on .NET clamps negatives to 0, so we
            // must round to long first, then widen to ulong via the bit
            // pattern, then mask to the signal's bit width.
            ValueType.Signed => (ulong)(long)Math.Round(raw, MidpointRounding.AwayFromZero) & ((1UL << signal.Length) - 1UL),
            // For float / double, the decoder reinterprets the bit pattern
            // as IEEE-754 and then applies factor+offset. We must do the
            // same: convert the engineering raw to (float)/(double), then
            // push the IEEE bits through the wire. (DBC convention is that
            // Float/Double are Vector extensions; factor/offset still
            // apply, but the wire value is the float bits.)
            ValueType.Float => (ulong)BitConverter.SingleToInt32Bits((float)raw) & 0xFFFFFFFFUL,
            ValueType.Double => (ulong)BitConverter.DoubleToInt64Bits(raw),
            _ => (ulong)Math.Round(raw, MidpointRounding.AwayFromZero),
        };
    }

    private static void WriteSignal(byte[] bytes, Signal signal, ulong raw)
    {
        if (signal.Order == ByteOrder.LittleEndian)
            WriteLittleEndian(bytes, signal.StartBit, signal.Length, raw);
        else
            WriteBigEndian(bytes, signal.StartBit, signal.Length, raw);
    }

    private static void WriteLittleEndian(byte[] bytes, ushort start, byte length, ulong value)
    {
        // Inverse of SignalDecoder.ReadLittleEndian: bit i of value → byte start/8+i/8, bit i%8
        for (int i = 0; i < length; i++)
        {
            int absBit = start + i;
            int byteIdx = absBit / 8;
            int bitIdx = absBit % 8;
            if (byteIdx >= bytes.Length) return;
            var bit = (byte)((value >> i) & 1UL);
            if (bit == 1)
                bytes[byteIdx] |= (byte)(1 << bitIdx);
            else
                bytes[byteIdx] &= (byte)~(1 << bitIdx);
        }
    }

    private static void WriteBigEndian(byte[] bytes, ushort start, byte length, ulong value)
    {
        // Inverse of SignalDecoder.ReadBigEndian: bit i of result was (value >> (length-1-i)) & 1
        for (int i = 0; i < length; i++)
        {
            int absBit = start + i;
            int byteIdx = absBit / 8;
            int bitInByte = 7 - (absBit % 8);
            if (byteIdx >= bytes.Length) return;
            var bit = (byte)((value >> (length - 1 - i)) & 1UL);
            if (bit == 1)
                bytes[byteIdx] |= (byte)(1 << bitInByte);
            else
                bytes[byteIdx] &= (byte)~(1 << bitInByte);
        }
    }
}
