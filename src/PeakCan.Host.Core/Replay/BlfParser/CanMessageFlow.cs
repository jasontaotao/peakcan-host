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
        // v3.51.0 T6.5 PATCH (reviewer finding #3 — HIGH): map Vector's
        // 1-byte can_flags to our FrameFlags enum. Per vblf_can.py the
        // canonical layout is:
        //   bit 0 = RTR (CAN 2.0 remote transmission request)
        //   bit 1 = BRS (CAN FD bit-rate switch — only meaningful on FD;
        //              ignored on classic CAN because the bus hadn't
        //              switched dataphase baud)
        // We deliberately do NOT set FrameFlags.Fd here even when
        // (flags & 0x02) is set — the canfd-ness comes from ObjType
        // (100/101), not from this byte. Sister of CanFdMessageFlow
        // which hard-codes Fd regardless of can_flags.
        var ff = FrameFlags.None;
        if ((flags & 0x01) != 0) ff |= FrameFlags.Rtr;
        return new ReplayFrame(timestamp / BlfFormat.TimestampScale, frameId, dlc, data, ff);
    }
}
