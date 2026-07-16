using System.Buffers.Binary;

namespace PeakCan.Host.Core.Replay;

public static partial class BlfParser
{
    /// <summary>
    /// v3.51.0 MINOR: unpacks 16-byte CanMessage frame (HBBI8s struct format).
    /// Sister of vblf_can.py:14 _FORMAT = struct.Struct("HBBI8s") = 16 bytes.
    /// </summary>
    internal static ReplayFrame CanMessageFlow_Unpack(ulong timestamp, ReadOnlySpan<byte> frameData)
    {
        if (frameData.Length < BlfFormat.CanMessageDataSize)
        {
            throw new ReplayFormatException(
                $"CanMessage frame too small: {frameData.Length} < {BlfFormat.CanMessageDataSize}");
        }
        ushort channel = BinaryPrimitives.ReadUInt16LittleEndian(frameData);
        byte flags = frameData[2];
        byte dlc = frameData[3];
        uint frameId = BinaryPrimitives.ReadUInt32LittleEndian(frameData.Slice(4));
        var data = frameData.Slice(8, 8).ToArray();
        return new ReplayFrame(timestamp / BlfFormat.TimestampScale, frameId, dlc, data, FrameFlags.None);
    }
}
