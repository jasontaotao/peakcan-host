using System.Buffers.Binary;
using PeakCan.HIL.Core;

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
        // BLF bit 31 = extended-format marker (see CanMessageFlow). Mask to bare ID.
        const uint extendedBit = 0x80000000;
        const uint idMask = 0x1FFFFFFF;
        var isExtended = (frameId & extendedBit) != 0;
        var rawId = frameId & idMask;
        var data = frameData.Slice(8, 8).ToArray();
        // Trailer (IBBH = 4+2+1+1 = 8 bytes) at offset 16 — skipped (debug info)
        // can_flags 位语义同 CanMessageFlow（python-can can/io/blf.py:93-98）：
        //   bit 0 (0x01) = DIR，bit 7 (0x80) = REMOTE_FLAG。
        var ff = FrameFlags.None;
        if ((flags & 0x80) != 0) ff |= FrameFlags.Rtr;
        var isTransmit = (flags & 0x01) != 0;
        return new ReplayFrame(timestamp / BlfFormat.TimestampScale, rawId, dlc, data, ff, isExtended,
            channel, isTransmit);
    }
}

