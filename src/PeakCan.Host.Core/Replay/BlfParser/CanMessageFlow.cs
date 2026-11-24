using System.Buffers.Binary;
using PeakCan.HIL.Core;

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
        // Vector BLF spec (verified 1:1 against python-can BLFReader): bit 31
        // of frame_id is the extended-format marker, low 29 bits are the real
        // CAN ID. Mask here so ReplayFrame.Id is always the bare value and
        // consumers read IsExtended instead of re-deriving from bit pattern.
        const uint extendedBit = 0x80000000;
        const uint idMask = 0x1FFFFFFF;
        var isExtended = (frameId & extendedBit) != 0;
        var rawId = frameId & idMask;
        var data = frameData.Slice(8, 8).ToArray();
        // can_flags 位语义（权威来源：python-can 的 can/io/blf.py:93-98 与
        // vblf_constants.py 的 CanFdFlags）：
        //   bit 0 (0x01) = DIR         — 1 = 本机 Tx
        //   bit 7 (0x80) = REMOTE_FLAG — 1 = RTR 远程帧
        //
        // 早期实现把 bit0 当 RTR，后果是**每个 Tx 帧都被当成远程帧丢弃**：
        // 真实 trace（微卡26商改网络测试NM_16测试Log.blf）里 PMS 帧 flags=0x01
        // 且带 8 字节有效数据，被丢弃后 NM_NID_MISMATCH / NM_RMR_SET_WHILE_DISABLED
        // 两条基线 finding 永远不产出（spec §14.4 / §14.6）。
        //
        // 不设 FrameFlags.Fd：FD 与否由 ObjType（100/101）决定，不是这个字节。
        var ff = FrameFlags.None;
        if ((flags & 0x80) != 0) ff |= FrameFlags.Rtr;
        var isTransmit = (flags & 0x01) != 0;
        return new ReplayFrame(timestamp / BlfFormat.TimestampScale, rawId, dlc, data, ff, isExtended,
            channel, isTransmit);
    }
}

