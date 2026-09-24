namespace PeakCan.Host.Core.Xcp.Protocol;

/// <summary>
/// XCP 线上 EVENT_CHANNEL_TIME_UNIT 换算表（GET_DAQ_EVENT_INFO 正响应 byte[6]，
/// S2-T8 探针职责——对账引擎只消费换算后的 µs，绝不做线上换算）。
/// <para>
/// 线上表 0=1ns 起：0=1ns, 1=10ns, 2=100ns, 3=1µs, 4=10µs, 5=100µs, 6=1ms,
/// 7=10ms, 8=100ms, 9=1s。<b>与 A2L 侧 A2ML EVENT.TIME_UNIT 枚举不是一回事</b>
/// （两套编号体系，spec §1 坑）：A2L 声明值的换算归 a2l-editor（PeriodMicroseconds），
/// 线上值的换算归本表——两边换出的 µs 才允许比对，禁止拿原始字节码直比。
/// </para>
/// <para>
/// 亚微秒档（0/1/2）换不成整数 µs → 拒算（宁缺不猜，对齐包侧对 &lt;1µs 的处理）；
/// EVENT_CYCLE=0（周期未知/事件同步）与表外编号同样拒算。
/// </para>
/// </summary>
public static class XcpWireTimeUnit
{
    /// <summary>线上编号 → 每 tick 微秒系数；null = 亚微秒档，整数 µs 表达不了。</summary>
    private static readonly uint?[] MicrosecondsPerTick =
        [null, null, null, 1, 10, 100, 1_000, 10_000, 100_000, 1_000_000];

    /// <summary>
    /// 线上 (EVENT_CYCLE, EVENT_CHANNEL_TIME_UNIT) → 整数微秒。
    /// 返回 false 的情形（调用侧按"未测得"处理，交对账拒绝）：周期未知（cycle=0）、
    /// 亚微秒档、表外编号、乘积溢出 uint——一律不猜。
    /// </summary>
    public static bool TryConvertMicroseconds(byte eventCycle, byte timeUnitCode, out uint periodMicroseconds)
    {
        periodMicroseconds = 0;
        if (eventCycle == 0)
            return false;
        if (timeUnitCode >= MicrosecondsPerTick.Length)
            return false;
        if (MicrosecondsPerTick[timeUnitCode] is not { } microsecondsPerTick)
            return false;

        var total = (ulong)eventCycle * microsecondsPerTick;
        if (total > uint.MaxValue)
            return false;

        periodMicroseconds = (uint)total;
        return true;
    }
}
