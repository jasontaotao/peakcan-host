using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using Xunit;
using DbcValueType = PeakCan.HIL.Core.Dbc.ValueType;

namespace PeakCan.HIL.Core.Tests.J1939;

/// <summary>
/// spec §13 Task-0 spike：验证 L3 方案的两个复用假设。
/// ① SignalDecoder.Decode 对 &gt;8 字节数据（49B BRM）无 64-bit 截断/异常；
/// ② DBC Message.Id 的 bit31 IDE 约定可被 &amp; 0x1FFFFFFF 归一化为裸 29 位 ID。
/// </summary>
public class J1939SpikeTests
{
    [Fact]
    public void SignalDecoder_Decodes_49Byte_Brm_Payload()
    {
        // BRM §11.1.4：49 字节，SOC 信号放在第 48 字节（0-based），0x32 = 50%
        var payload = new byte[49];
        payload[48] = 0x32;
        var signal = new Signal("SOC", StartBit: 384, Length: 8, ByteOrder.LittleEndian,
            DbcValueType.Unsigned, Factor: 1.0, Offset: 0.0, Min: 0, Max: 100, Unit: "%",
            Receivers: Array.Empty<string>());

        double value = SignalDecoder.Decode(payload, signal);

        value.Should().Be(50.0);
    }

    [Theory]
    [InlineData(0x980256F4u, 0x180256F4u)]  // 扩展帧 DBC Id（bit31 置位）→ 裸 29 位
    [InlineData(0x180256F4u, 0x180256F4u)]  // 已是裸 ID（部分 J1939 DBC 惯例）→ 不变
    public void Dbc_Message_Id_Normalizes_To_Raw_29Bit(uint dbcId, uint expected)
        => (dbcId & 0x1FFFFFFFu).Should().Be(expected);
}
