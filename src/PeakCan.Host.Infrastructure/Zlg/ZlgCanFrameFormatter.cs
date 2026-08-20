using System.Runtime.InteropServices;
using PeakCan.HIL.Core;

namespace PeakCan.Host.Infrastructure.Zlg;

/// <summary>
/// ZLG 帧格式工具：DLC 转换、帧编码/解码。
/// ZLG 的 CAN FD DLC 编码与标准 CAN FD 相同（0-8 字面量，9-15 映射到 12-64 字节）。
/// </summary>
internal static class ZlgCanFrameFormatter
{
    /// <summary>CAN FD DLC 码 → 实际字节数。</summary>
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

    /// <summary>实际字节数 → CAN FD DLC 码（反向映射）。</summary>
    public static byte BytesToDlc(byte len) => len switch
    {
        <= 8 => len,
        <= 12 => 9,
        <= 16 => 10,
        <= 20 => 11,
        <= 24 => 12,
        <= 32 => 13,
        <= 48 => 14,
        _ => 15,
    };

    /// <summary>将经典 ZLG CAN_OBJ 解码为 CanFrame（标准帧或扩展帧，不含 FD）。</summary>
    public static CanFrame DecodeClassic(ChannelId channel, ZlgCanMsg msg, Timestamp ts)
    {
        var format = msg.ExternFlag != 0 ? FrameFormat.Extended : FrameFormat.Standard;
        var canId = new CanId(msg.ID, format);
        var len = Math.Min(msg.DataLen, (byte)8);
        var data = new byte[len];
        if (len > 0 && msg.Data is not null)
            Array.Copy(msg.Data, data, len);
        var flags = FrameFlags.None;
        if (msg.RemoteFlag != 0) flags |= FrameFlags.Rtr;
        return new CanFrame(canId, data, flags, channel, ts);
    }

    /// <summary>将 ZLG CAN FD 帧解码为 CanFrame。</summary>
    public static CanFrame DecodeFd(ChannelId channel, ZlgCanFdMsg msg, Timestamp ts)
    {
        var format = msg.ExternFlag != 0 ? FrameFormat.Extended : FrameFormat.Standard;
        var canId = new CanId(msg.ID, format);
        var dlc = DlcToBytes(msg.DataLen);
        var data = new byte[dlc];
        if (dlc > 0 && msg.Data is not null)
            Array.Copy(msg.Data, data, dlc);
        var flags = FrameFlags.Fd;
        // BRS 和 ESI 标志位：ZLG 在 uReserved0 的低 2 位编码。
        // 0x01 = BRS, 0x02 = ESI。具体位需对照 ZLG 驱动版本确认。
        if ((msg.Reserved0 & 0x01) != 0) flags |= FrameFlags.BitRateSwitch;
        if ((msg.Reserved0 & 0x02) != 0) flags |= FrameFlags.ErrorStateIndicator;
        if (msg.RemoteFlag != 0) flags |= FrameFlags.Rtr;
        return new CanFrame(canId, data, flags, channel, ts);
    }

    /// <summary>将 CanFrame 编码为经典 ZLG CAN_OBJ。</summary>
    public static ZlgCanMsg EncodeClassic(CanFrame frame)
    {
        var len = Math.Min(frame.Dlc, (byte)8);
        var data8 = new byte[8];
        if (len > 0) frame.Data.Span.CopyTo(data8.AsSpan(0, len));
        return new ZlgCanMsg
        {
            ID = frame.Id.Raw,
            SendType = 0,
            RemoteFlag = (byte)((frame.Flags & FrameFlags.Rtr) != 0 ? 1 : 0),
            ExternFlag = (byte)(frame.Id.IsExtended ? 1 : 0),
            DataLen = len,
            Data = data8,
            Reserved = new byte[3],
        };
    }

    /// <summary>将 CanFrame 编码为 ZLG CAN FD 帧。</summary>
    public static ZlgCanFdMsg EncodeFd(CanFrame frame)
    {
        var len = Math.Min(frame.Dlc, (byte)64);
        var data64 = new byte[64];
        if (len > 0) frame.Data.Span.CopyTo(data64.AsSpan(0, len));
        var reserved0 = (byte)0;
        if ((frame.Flags & FrameFlags.BitRateSwitch) != 0) reserved0 |= 0x01;
        if ((frame.Flags & FrameFlags.ErrorStateIndicator) != 0) reserved0 |= 0x02;
        return new ZlgCanFdMsg
        {
            ID = frame.Id.Raw,
            SendType = 0,
            RemoteFlag = (byte)((frame.Flags & FrameFlags.Rtr) != 0 ? 1 : 0),
            ExternFlag = (byte)(frame.Id.IsExtended ? 1 : 0),
            DataLen = BytesToDlc(len),
            Data = data64,
            Reserved0 = reserved0,
        };
    }
}