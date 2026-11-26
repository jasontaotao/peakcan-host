using PeakCan.HIL.Core;

namespace PeakCan.Host.Infrastructure.Zlg;

/// <summary>
/// ZLG 帧格式工具。ZCAN 的 can_frame.can_id 用 bit31/30/29 编码 EFF/RTR/ERR，
/// 与 <see cref="CanId.Raw"/>（纯 ID 位）不同，编解码时必须显式转换。
/// <para>canfd_frame.len 是实际字节数（0..64），不是 DLC 码。</para>
/// </summary>
internal static class ZlgCanFrameFormatter
{
    public static CanFrame DecodeClassic(ChannelId channel, in ZcanCanFrame frame, Timestamp ts)
    {
        var canId = DecodeCanId(frame.CanId);
        var len = Math.Min((int)frame.CanDlc, 8);
        var data = new byte[len];
        if (len > 0 && frame.Data is not null)
            Array.Copy(frame.Data, data, len);

        var flags = FrameFlags.None;
        if ((frame.CanId & ZcanCanIdFlags.Remote) != 0) flags |= FrameFlags.Rtr;
        if ((frame.CanId & ZcanCanIdFlags.Error) != 0) flags |= FrameFlags.ErrFrame;

        return new CanFrame(canId, data, flags, channel, ts);
    }

    public static CanFrame DecodeFd(ChannelId channel, in ZcanCanFdFrame frame, Timestamp ts)
    {
        var canId = DecodeCanId(frame.CanId);
        var len = Math.Min((int)frame.Len, 64);
        var data = new byte[len];
        if (len > 0 && frame.Data is not null)
            Array.Copy(frame.Data, data, len);

        var flags = FrameFlags.Fd;
        if ((frame.Flags & ZcanCanFdFlags.Brs) != 0) flags |= FrameFlags.BitRateSwitch;
        if ((frame.Flags & ZcanCanFdFlags.Esi) != 0) flags |= FrameFlags.ErrorStateIndicator;
        if ((frame.CanId & ZcanCanIdFlags.Remote) != 0) flags |= FrameFlags.Rtr;
        if ((frame.CanId & ZcanCanIdFlags.Error) != 0) flags |= FrameFlags.ErrFrame;

        return new CanFrame(canId, data, flags, channel, ts);
    }

    public static ZcanTransmitData EncodeClassic(CanFrame frame)
    {
        var len = Math.Min((int)frame.Dlc, 8);
        var copy = Math.Min(len, frame.Data.Length);
        var data = new byte[8];
        if (copy > 0) frame.Data.Span[..copy].CopyTo(data);

        return new ZcanTransmitData
        {
            Frame = new ZcanCanFrame
            {
                CanId = EncodeCanId(frame),
                CanDlc = (byte)copy,
                Data = data,
            },
            TransmitType = ZcanTransmitType.Normal,
        };
    }

    public static ZcanTransmitFdData EncodeFd(CanFrame frame)
    {
        var rawLen = Math.Min((int)frame.Dlc, 64);
        var len = NormalizeFdLength(rawLen);
        var copy = Math.Min(rawLen, frame.Data.Length);
        var data = new byte[64];
        if (copy > 0) frame.Data.Span[..copy].CopyTo(data);

        var flags = (byte)0;
        if ((frame.Flags & FrameFlags.BitRateSwitch) != 0) flags |= ZcanCanFdFlags.Brs;
        if ((frame.Flags & FrameFlags.ErrorStateIndicator) != 0) flags |= ZcanCanFdFlags.Esi;

        return new ZcanTransmitFdData
        {
            Frame = new ZcanCanFdFrame
            {
                CanId = EncodeCanId(frame),
                Len = (byte)len,
                Flags = flags,
                Data = data,
            },
            TransmitType = ZcanTransmitType.Normal,
        };
    }

    /// <summary>把实际字节数规范化为合法的 CAN FD 长度。</summary>
    public static int NormalizeFdLength(int len) => len switch
    {
        <= 0 => 0,
        <= 8 => len,
        <= 12 => 12,
        <= 16 => 16,
        <= 20 => 20,
        <= 24 => 24,
        <= 32 => 32,
        <= 48 => 48,
        _ => 64,
    };

    private static CanId DecodeCanId(uint raw)
    {
        var extended = (raw & ZcanCanIdFlags.Extended) != 0;
        var id = raw & ZcanCanIdFlags.IdMask;
        return new CanId(id, extended ? FrameFormat.Extended : FrameFormat.Standard);
    }

    private static uint EncodeCanId(CanFrame frame)
    {
        var raw = frame.Id.Raw;
        if (frame.Id.IsExtended) raw |= ZcanCanIdFlags.Extended;
        if ((frame.Flags & FrameFlags.Rtr) != 0) raw |= ZcanCanIdFlags.Remote;
        if ((frame.Flags & FrameFlags.ErrFrame) != 0) raw |= ZcanCanIdFlags.Error;
        return raw;
    }
}
