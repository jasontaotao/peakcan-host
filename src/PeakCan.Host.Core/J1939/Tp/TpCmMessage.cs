namespace PeakCan.HIL.Core.J1939;

/// <summary>TP.CM 控制字节（J1939-21 §5.3）。</summary>
public enum TpCmControl : byte
{
    Rts = 0x10,
    Cts = 0x11,
    EomAck = 0x13,
    Bam = 0x20,
    ConnAbort = 0xFF,
}

/// <summary>
/// TP.CM 帧（PGN 0x00EC00，PDU1，PS=目标地址/BAM 时 0xFF）的编解码。
/// 字节布局（0-indexed，spec §5.2 修订版）：[0] 控制字节；RTS/EOM/BAM：[1..2] 总长 LE、
/// [3] 总包数、[4] RTS=发送方每 CTS 上限 / 其余 0xFF；CTS：[1] 每CTS包数、[2] 起始包号、[3..4]=0xFF；
/// Abort：[1] 原因；[5..7] PGN 小端（PDU1 时 [5]=0）。PGN 小端编解码收敛于此（spec §16.4）。
/// </summary>
public readonly record struct TpCmMessage(
    TpCmControl Control,
    ushort TotalSize,
    byte TotalPackets,
    byte MaxPacketsPerCts,
    byte NextPacketNumber,
    byte AbortReason,
    uint Pgn)
{
    public static TpCmMessage Rts(ushort totalSize, byte totalPackets, byte maxPacketsPerCts, uint pgn)
        => new(TpCmControl.Rts, totalSize, totalPackets, maxPacketsPerCts, 0, 0, pgn);

    public static TpCmMessage Cts(byte maxPackets, byte nextPacket, uint pgn)
        => new(TpCmControl.Cts, 0, 0, maxPackets, nextPacket, 0, pgn);

    public static TpCmMessage EomAck(ushort totalSize, byte totalPackets, uint pgn)
        => new(TpCmControl.EomAck, totalSize, totalPackets, 0, 0, 0, pgn);

    public static TpCmMessage Bam(ushort totalSize, byte totalPackets, uint pgn)
        => new(TpCmControl.Bam, totalSize, totalPackets, 0, 0, 0, pgn);

    public static TpCmMessage Abort(byte reason, uint pgn)
        => new(TpCmControl.ConnAbort, 0, 0, 0, 0, reason, pgn);

    /// <summary>编码为 8 字节 CAN 数据。</summary>
    public byte[] Encode()
    {
        var b = new byte[8];
        b[0] = (byte)Control;
        switch (Control)
        {
            case TpCmControl.Rts:
            case TpCmControl.EomAck:
            case TpCmControl.Bam:
                b[1] = (byte)(TotalSize & 0xFF);
                b[2] = (byte)(TotalSize >> 8);
                b[3] = TotalPackets;
                b[4] = Control == TpCmControl.Rts ? MaxPacketsPerCts : (byte)0xFF;
                break;
            case TpCmControl.Cts:
                b[1] = MaxPacketsPerCts;
                b[2] = NextPacketNumber;
                b[3] = 0xFF;
                b[4] = 0xFF;
                break;
            case TpCmControl.ConnAbort:
                b[1] = AbortReason;
                b[2] = 0xFF;
                b[3] = 0xFF;
                b[4] = 0xFF;
                break;
        }

        b[5] = (byte)(Pgn & 0xFF);
        b[6] = (byte)((Pgn >> 8) & 0xFF);
        b[7] = (byte)((Pgn >> 16) & 0xFF);
        return b;
    }

    /// <summary>解码；不足 8 字节或未知控制字节抛 <see cref="ArgumentException"/>（sink adapter 窄捕获契约）。</summary>
    public static TpCmMessage Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8)
            throw new ArgumentException("TP.CM requires 8 data bytes.", nameof(data));

        var control = (TpCmControl)data[0];
        if (control is not (TpCmControl.Rts or TpCmControl.Cts or TpCmControl.EomAck or TpCmControl.Bam or TpCmControl.ConnAbort))
            throw new ArgumentException($"Unknown TP.CM control byte 0x{data[0]:X2}.", nameof(data));

        uint pgn = data[5] | ((uint)data[6] << 8) | ((uint)data[7] << 16);
        ushort size = (ushort)(data[1] | (data[2] << 8));
        return control switch
        {
            TpCmControl.Rts => new TpCmMessage(control, size, data[3], data[4], 0, 0, pgn),
            TpCmControl.Cts => new TpCmMessage(control, 0, 0, data[1], data[2], 0, pgn),
            TpCmControl.EomAck or TpCmControl.Bam => new TpCmMessage(control, size, data[3], 0, 0, 0, pgn),
            _ => new TpCmMessage(control, 0, 0, 0, 0, data[1], pgn),
        };
    }
}
