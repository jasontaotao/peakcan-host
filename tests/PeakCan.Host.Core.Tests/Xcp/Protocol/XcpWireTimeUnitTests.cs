using PeakCan.Host.Core.Xcp.Protocol;

namespace PeakCan.Host.Core.Tests.Xcp.Protocol;

/// <summary>
/// XcpWireTimeUnit：XCP 线上 EVENT_CHANNEL_TIME_UNIT 表换算（S2-T8 探针职责）。
/// <para>
/// 线上表 0=1ns 起——与 A2L 侧 A2ML EVENT.TIME_UNIT 枚举**不是一回事**（两套编号
/// 体系，禁止直比；spec §1 坑）。亚微秒档（线上 0/1/2 = 1ns/10ns/100ns）无法用
/// 整数 µs 表达 → 拒算（宁缺不猜，对齐包侧 a2l-editor 对 &lt;1µs 的处理）。
/// </para>
/// </summary>
public class XcpWireTimeUnitTests
{
    [Fact]
    public void Wire_cycle_0x0a_time_unit_6_converts_to_10000us()
    {
        // 模拟从机黄金样本 (EVENT_CYCLE=0x0A, TIME_UNIT=6)：线上表 6=1ms → 10×1000µs = 10000µs（100Hz，spec §1）。
        Assert.True(XcpWireTimeUnit.TryConvertMicroseconds(0x0A, 0x06, out var us));
        Assert.Equal(10000u, us);
    }

    [Fact]
    public void Wire_time_unit_0_is_1ns_sub_microsecond_rejected()
    {
        // 线上表 0=1ns：(01,00) = 1ns——整数 µs 表达不了，必须拒算。
        // 历史注释"线上 (01,00) 换算=100Hz"是错的：10000µs 是 A2L 侧值，两者不是一回事。
        Assert.False(XcpWireTimeUnit.TryConvertMicroseconds(0x01, 0x00, out _));
    }

    [Fact]
    public void Wire_time_units_1_and_2_are_also_sub_microsecond()
    {
        Assert.False(XcpWireTimeUnit.TryConvertMicroseconds(1, 1, out _));   // 10ns
        Assert.False(XcpWireTimeUnit.TryConvertMicroseconds(1, 2, out _));   // 100ns
    }

    [Fact]
    public void Wire_event_cycle_zero_has_no_period_semantics()
    {
        // TIME_CYCLE=0 = 周期未知/事件同步——换不出周期，必须拒算。
        Assert.False(XcpWireTimeUnit.TryConvertMicroseconds(0x00, 0x06, out _));
    }

    [Fact]
    public void Wire_time_units_three_to_nine_convert_with_expected_factors()
    {
        Assert.True(XcpWireTimeUnit.TryConvertMicroseconds(3, 3, out var us3));
        Assert.Equal(3u, us3);            // 3 × 1µs
        Assert.True(XcpWireTimeUnit.TryConvertMicroseconds(5, 4, out var us4));
        Assert.Equal(50u, us4);           // 5 × 10µs
        Assert.True(XcpWireTimeUnit.TryConvertMicroseconds(1, 5, out var us5));
        Assert.Equal(100u, us5);          // 1 × 100µs
        Assert.True(XcpWireTimeUnit.TryConvertMicroseconds(2, 7, out var us7));
        Assert.Equal(20000u, us7);        // 2 × 10ms
        Assert.True(XcpWireTimeUnit.TryConvertMicroseconds(1, 8, out var us8));
        Assert.Equal(100000u, us8);       // 1 × 100ms
        Assert.True(XcpWireTimeUnit.TryConvertMicroseconds(1, 9, out var us9));
        Assert.Equal(1000000u, us9);      // 1 × 1s
    }

    [Fact]
    public void Wire_time_unit_out_of_table_is_rejected()
    {
        Assert.False(XcpWireTimeUnit.TryConvertMicroseconds(1, 10, out _));
    }
}
