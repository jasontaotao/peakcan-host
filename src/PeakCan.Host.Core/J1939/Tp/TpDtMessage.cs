namespace PeakCan.HIL.Core.J1939;

/// <summary>
/// TP.DT 帧（PGN 0x00EB00，PDU1）编解码。Encode 将不足 7 字节的末帧以 0xFF 填充
/// （J1939-21 §8.7：未用位/字节填 1）；Decode 恒保留 7 字节（是否按 TotalSize 截尾由重组层决定）。
/// </summary>
public readonly record struct TpDtMessage(byte SequenceNumber, ReadOnlyMemory<byte> Data)
{
    /// <summary>编码为 8 字节 CAN 数据；序号 1..255、数据 ≤7 字节。</summary>
    /// <exception cref="ArgumentException">序号为 0 或数据 &gt; 7 字节。</exception>
    public byte[] Encode()
    {
        if (SequenceNumber == 0)
            throw new ArgumentException("TP.DT sequence number is 1-based (1..255).", nameof(SequenceNumber));
        if (Data.Length > 7)
            throw new ArgumentException("TP.DT carries at most 7 payload bytes.", nameof(Data));

        var b = new byte[8];
        b[0] = SequenceNumber;
        Data.Span.CopyTo(b.AsSpan(1));
        for (int i = 1 + Data.Length; i < 8; i++)
            b[i] = 0xFF;
        return b;
    }

    /// <summary>解码；不足 8 字节抛 <see cref="ArgumentException"/>。</summary>
    public static TpDtMessage Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8)
            throw new ArgumentException("TP.DT requires 8 data bytes.", nameof(data));

        return new TpDtMessage(data[0], data.Slice(1, 7).ToArray());
    }
}
