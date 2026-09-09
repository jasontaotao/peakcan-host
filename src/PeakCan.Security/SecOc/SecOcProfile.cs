namespace PeakCan.Security.SecOc;

/// <summary>
/// 单个 Secured I-PDU 的线格式参数（spec §6.1，对应通信矩阵一行的SecOC扩展列）。
/// 与样例矩阵字段一一对应：fvLen 截断位数、macLen 截断位数、DataId。
/// </summary>
public sealed record SecOcProfile
{
    /// <summary>参与 MAC 计算的 PDU 标识（16bit，全网唯一，spec §6.1）。</summary>
    public required ushort DataId { get; init; }

    /// <summary>线上传输的 freshness 低位数（8 的倍数，常见 16）。</summary>
    public required int FvLenBits { get; init; }

    /// <summary>线上传输的 MAC 高位数（8 的倍数，常见 24）。</summary>
    public required int MacLenBits { get; init; }

    /// <summary>参与 MAC 计算的完整 freshness 位数（v1 固定 32）。</summary>
    public int FvFullBits { get; init; } = 32;
}