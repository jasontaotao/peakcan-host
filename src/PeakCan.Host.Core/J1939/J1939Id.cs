namespace PeakCan.HIL.Core.J1939;

/// <summary>
/// 29 位 J1939 标识符的分解与组合（spec §5.1）。
/// <para>所有 J1939 CAN ID 位运算收敛于此（spec §16.4）。29 位布局：
/// bits 28-26 优先级、bit 25 R/EDP、bit 24 DP、bits 23-16 PF、bits 15-8 PS、bits 7-0 SA。</para>
/// </summary>
public readonly record struct J1939Id(uint Raw)
{
    /// <summary>裸 29 位掩码（剥去 DBC bit31 IDE 约定位）。</summary>
    public const uint Raw29Mask = 0x1FFFFFFF;

    /// <summary>3 位优先级（0..7）。</summary>
    public byte Priority => (byte)((Raw >> 26) & 0x07);

    /// <summary>bit 25 保留位（GBT27930 恒 0）。</summary>
    public byte ReservedEdp => (byte)((Raw >> 25) & 0x01);

    /// <summary>bit 24 数据页。</summary>
    public byte DataPage => (byte)((Raw >> 24) & 0x01);

    /// <summary>bits 23-16 协议数据单元格式。</summary>
    public byte PduFormat => (byte)((Raw >> 16) & 0xFF);

    /// <summary>bits 15-8 协议数据单元特定字段（PDU1=目标地址，PDU2=组扩展）。</summary>
    public byte PduSpecific => (byte)((Raw >> 8) & 0xFF);

    /// <summary>bits 7-0 源地址。</summary>
    public byte SourceAddress => (byte)(Raw & 0xFF);

    /// <summary>PDU1（点对点，PS=目标地址）当 PF &lt; 0xF0。</summary>
    public bool IsPdu1 => PduFormat < 0xF0;

    /// <summary>18 位 PGN：bit17=R/EDP、bit16=DP、bits15-8=PF、bits7-0=PDU2 才含 PS。</summary>
    public uint Pgn => ((uint)ReservedEdp << 17) | ((uint)DataPage << 16) | ((uint)PduFormat << 8) | (IsPdu1 ? 0u : PduSpecific);

    /// <summary>PDU1 的目标地址；PDU2 无目标地址概念。</summary>
    public byte? DestinationAddress => IsPdu1 ? PduSpecific : null;

    /// <summary>PGN 的 PF 字节（组合入口用）。</summary>
    public static bool IsPdu1Pgn(uint pgn) => ((pgn >> 8) & 0xFF) < 0xF0;

    /// <summary>
    /// 唯一合法的 ID 组合入口。PDU1 必须提供 <paramref name="da"/>（目标地址落入 PS）
    /// 且 PGN 低 8 位必须为 0（PDU1 的 PS 不属于 PGN）；PDU2 禁止提供 da。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">priority &gt; 7 或 pgn &gt; 0x3FFFF。</exception>
    /// <exception cref="ArgumentException">PDU1/PDU2 的 da 使用不当或 PGN 非规范。</exception>
    public static uint Compose(byte priority, uint pgn, byte sa, byte? da = null)
    {
        if (priority > 7)
            throw new ArgumentOutOfRangeException(nameof(priority), priority, "J1939 priority is 3 bits (0..7).");
        if (pgn > 0x3FFFF)
            throw new ArgumentOutOfRangeException(nameof(pgn), pgn, "PGN is 18 bits (0..0x3FFFF).");

        bool pdu1 = IsPdu1Pgn(pgn);
        byte ps;
        if (pdu1)
        {
            if (da is null)
                throw new ArgumentException($"PDU1 PGN 0x{pgn:X6} requires a destination address (da).", nameof(da));
            if ((pgn & 0xFF) != 0)
                throw new ArgumentException($"PGN 0x{pgn:X6} is non-canonical for PDU1 (low byte must be 0).", nameof(pgn));
            ps = da.Value;
        }
        else
        {
            if (da is not null)
                throw new ArgumentException($"PDU2 PGN 0x{pgn:X6} carries its own group extension; da must be null.", nameof(da));
            ps = (byte)(pgn & 0xFF);
        }

        byte rEdp = (byte)((pgn >> 17) & 0x01);
        byte dp = (byte)((pgn >> 16) & 0x01);
        byte pf = (byte)((pgn >> 8) & 0xFF);
        return ((uint)priority << 26) | ((uint)rEdp << 25) | ((uint)dp << 24) | ((uint)pf << 16) | ((uint)ps << 8) | sa;
    }
}
