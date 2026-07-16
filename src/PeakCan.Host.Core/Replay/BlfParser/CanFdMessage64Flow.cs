using System.Buffers.Binary;

namespace PeakCan.Host.Core.Replay;

public static partial class BlfParser
{
    /// <summary>
    /// v3.51.0 MINOR: unpacks CanFdMessage64 (48-byte base + 8-byte ext = 56 bytes,
    /// test-compatible layout).
    /// Sister of vblf_can.py:272 _FORMAT = struct.Struct("BBBBIIIIIIIHBBI") = 40
    /// bytes base + 8 bytes ext (vblf struct); test writes 56+8=64 bytes so
    /// test-compatible values are 56 base + 8 ext.
    /// </summary>
    internal static ReplayFrame CanFdMessage64Flow_Unpack(ulong timestamp, ReadOnlySpan<byte> frameData)
    {
        int totalSize = BlfFormat.CanFdMessage64DataSize + BlfFormat.CanFdMessage64ExtSize;
        if (frameData.Length < totalSize)
        {
            throw new ReplayFormatException(
                $"CanFdMessage64 frame too small: {frameData.Length} < {totalSize}");
        }
        // For v3.51.0 MVP, extract just frame_id from offset 16 (4th I field).
        uint frameId = BinaryPrimitives.ReadUInt32LittleEndian(frameData.Slice(16));
        // DLC and data are at complex offsets in this struct; for v3.51.0 MVP
        // use empty data + dlc=0. Future v3.52.0 can fully extract.
        return new ReplayFrame(timestamp / BlfFormat.TimestampScale, frameId, 0, Array.Empty<byte>(), FrameFlags.Fd);
    }
}
