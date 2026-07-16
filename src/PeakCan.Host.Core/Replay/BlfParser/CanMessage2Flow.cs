using System.Buffers.Binary;

namespace PeakCan.Host.Core.Replay;

public static partial class BlfParser
{
    /// <summary>
    /// v3.51.0 MINOR: unpacks 24-byte CanMessage2 frame (HBBI8sIBBH).
    /// Sister of vblf_can.py:80 _FORMAT = struct.Struct("HBBI8sIBBH") = 24 bytes.
    /// </summary>
    internal static ReplayFrame CanMessage2Flow_Unpack(ulong timestamp, ReadOnlySpan<byte> frameData)
    {
        if (frameData.Length < BlfFormat.CanMessage2DataSize)
        {
            throw new ReplayFormatException(
                $"CanMessage2 frame too small: {frameData.Length} < {BlfFormat.CanMessage2DataSize}");
        }
        ushort channel = BinaryPrimitives.ReadUInt16LittleEndian(frameData);
        byte flags = frameData[2];
        byte dlc = frameData[3];
        uint frameId = BinaryPrimitives.ReadUInt32LittleEndian(frameData.Slice(4));
        var data = frameData.Slice(8, 8).ToArray();
        // Trailer (IBBH = 4+2+1+1 = 8 bytes) at offset 16 — skipped (debug info)
        return new ReplayFrame(timestamp / BlfFormat.TimestampScale, frameId, dlc, data, FrameFlags.None);
    }
}
