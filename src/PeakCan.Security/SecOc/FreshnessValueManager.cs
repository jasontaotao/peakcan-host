namespace PeakCan.Security.SecOc;

/// <summary>
/// per-PDU 内部单调计数器（spec §6.2 v1 语义：初始 0，每发一帧 +1；
/// 与真实 AUTOSAR 的 FM 同步制不等价，见 spec §2 范围声明）。
/// 线程安全性：单线程使用（每 PDU 一个实例，由调用方保证串行）。
/// </summary>
public sealed class FreshnessValueManager
{
    private uint _current;

    public FreshnessValueManager(uint initialFv = 0) => _current = initialFv;

    /// <summary>取当前完整 freshness 值并自增（首帧返回 0）。</summary>
    /// <remarks>uint 在 2^32 帧后回绕到 0——v1 闭环场景不可达（2^32 帧 @10ms 周期 ≈ 1.3 年），
    /// 不做处理；回绕后 RX 单调性判定会失效，属 v2（真实 FM 同步）职责。</remarks>
    public uint TakeNext() => _current++;

    /// <summary>
    /// 截断到低 <paramref name="bits"/> 位（spec §6.1：TruncFV 取最低有效位）。
    /// </summary>
    public static ushort TruncatedFor(int bits, uint value)
        => (ushort)(value & ((1u << bits) - 1));
}