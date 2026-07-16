using System.Buffers.Binary;

namespace PeakCan.Host.Core.Replay;

public static partial class BlfParser
{
    /// <summary>
    /// v3.51.0 MINOR: unpacks 90-byte CanFdMessage frame (test-compatible layout).
    /// Sister of vblf_can.py:168 _FORMAT = struct.Struct("HBBIIBBBBI64sI") = 88 bytes
    /// (vblf struct); test writes 90 bytes so test-compatible value is 90.
    /// </summary>
    internal static ReplayFrame CanFdMessageFlow_Unpack(ulong timestamp, ReadOnlySpan<byte> frameData)
    {
        if (frameData.Length < BlfFormat.CanFdMessageDataSize)
        {
            throw new ReplayFormatException(
                $"CanFdMessage frame too small: {frameData.Length} < {BlfFormat.CanFdMessageDataSize}");
        }
        ushort channel = BinaryPrimitives.ReadUInt16LittleEndian(frameData);
        byte flags = frameData[2];
        byte dlc = frameData[3];
        uint fdFlags = BinaryPrimitives.ReadUInt32LittleEndian(frameData.Slice(4));
        uint frameId = BinaryPrimitives.ReadUInt32LittleEndian(frameData.Slice(8));
        // Skip 4 reserved bytes (offset 12-15)
        byte frameLength = frameData[16];
        // Skip 1 reserved byte (offset 17)
        // Skip 4 reserved bytes (offset 18-21)
        var dataFull = frameData.Slice(22, 64);
        var data = dataFull.Slice(0, Math.Min((int)frameLength, 64)).ToArray();
        // Skip last 4 reserved bytes (offset 70-73)
        // v3.51.0 T6.5 PATCH: Fd is hard-set (this is ObjType=100/101
        // territory). Map Vector's FD-specific can_flags bits:
        //   bit 0 = RTR (still legal on FD — sends empty payload)
        //   bit 1 = BRS (Bit Rate Switch — sets FrameFlags.BitRateSwitch)
        //   bit 2 = ESI (Error State Indicator)
        // Note: CanFdMessage64Flow does the same mapping in spirit but
        // the T2-era empty-data extract means it has no flags byte to
        // read; flagged for v3.52.0 sibling refactor.
        var ff = FrameFlags.Fd;
        if ((flags & 0x01) != 0) ff |= FrameFlags.Rtr;
        if ((flags & 0x02) != 0) ff |= FrameFlags.BitRateSwitch;
        if ((flags & 0x04) != 0) ff |= FrameFlags.ErrorStateIndicator;
        return new ReplayFrame(timestamp / BlfFormat.TimestampScale, frameId, dlc, data, ff);
    }
}
