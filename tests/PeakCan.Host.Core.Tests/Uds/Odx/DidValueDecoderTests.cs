using System.Collections.Generic;
using FluentAssertions;
using PeakCan.Host.Core.Uds.Odx;

namespace PeakCan.Host.Core.Tests.Uds.Odx;

/// <summary>
/// v3.49.0 MINOR: T3.1 — <see cref="DidValueDecoder"/> 解码单元测试
/// (核心价值点: DID 数据类型识别闭环验证)。
///
/// 合成 byte[] 喂解码器，断言:
///   - ASCII 字符串 DID (VIN 17 字节) → 17 字符 US-ASCII 字符串
///   - UNICODE2 字符串 (UTF-16BE) → 解码
///   - UINT32 标量 + LINEAR 物理换算 → raw * A + B (温度/电压)
///   - UINT32 标量 + TEXTTABLE 枚举 → 文本标签
///   - BYTEFIELD → hex 透传
///   - 字段缺 BitLength / payload 不足 → 安全回退不抛异常
/// </summary>
public class DidValueDecoderTests
{
    private static DidField Field(string name, int bitLen, int off,
        DidBaseType t, CompuMethod? compu = null, DidUnit? unit = null) =>
        new(name, bitLen, off, t, compu, unit);

    [Fact]
    public void Decode_AsciiString_ReturnsDecodedString()
    {
        var field = Field("VIN", 17 * 8, 0, DidBaseType.AsciiString);
        var payload = System.Text.Encoding.ASCII.GetBytes("1HGCM82633A123456");

        var result = DidValueDecoder.Decode(payload, new[] { field });

        result.Should().ContainSingle();
        result[0].Name.Should().Be("VIN");
        result[0].PhysicalValue.Should().Be("1HGCM82633A123456");
        // raw 显示为完整 hex
        result[0].RawValue.Should().Contain("31 48"); // '1'=0x31 'H'=0x48
    }

    [Fact]
    public void Decode_UInt32Linear_Temperature_AppliesCoeffs()
    {
        // physical = 0.5 * raw - 40 (温度 did)
        var unit = new DidUnit("_C", "°C");
        var compu = CompuMethod.LinearOf(0.5, -40.0);
        var field = Field("Temp", 16, 0, DidBaseType.UInt32, compu, unit);
        // raw = 0x0064 = 100 → physical = 0.5*100-40 = 10 °C
        var payload = new byte[] { 0x00, 0x64 };

        var result = DidValueDecoder.Decode(payload, new[] { field });

        result.Should().ContainSingle();
        result[0].PhysicalValue.Should().Be("10°C");
        result[0].Unit.Should().Be("°C");
        result[0].RawValue.Should().Be("0x0064");
    }

    [Fact]
    public void Decode_UInt32Linear_Percent_DenominatorCoeffs()
    {
        // Percent_1Byte: physical = 100*raw / 255 → A = 100/255 ≈ 0.3922
        var compu = CompuMethod.LinearOf(100.0 / 255.0, 0.0);
        var unit = new DidUnit("Percent", "%");
        var field = Field("Percent", 8, 0, DidBaseType.UInt32, compu, unit);
        var payload = new byte[] { 0xFF }; // raw = 255 → 100%

        var result = DidValueDecoder.Decode(payload, new[] { field });

        result[0].PhysicalValue.Should().Be("100%");
    }

    [Fact]
    public void Decode_Texttable_ReturnsLabelForRaw()
    {
        var table = new Dictionary<long, string>
        {
            [0] = "false",
            [1] = "true",
        };
        var compu = CompuMethod.TexttableOf(table);
        var field = Field("TestFailed", 1, 0, DidBaseType.UInt32, compu);
        // raw bit = 1 → "true"
        var payload = new byte[] { 0x01 };

        var result = DidValueDecoder.Decode(payload, new[] { field });

        result[0].PhysicalValue.Should().Be("true");
    }

    [Fact]
    public void Decode_TexttableMissesLabel_FallsBackToRaw()
    {
        var table = new Dictionary<long, string>
        {
            [0] = "false",
            [1] = "true",
        };
        var compu = CompuMethod.TexttableOf(table);
        var field = Field("Status", 4, 0, DidBaseType.UInt32, compu);
        var payload = new byte[] { 0x07 }; // raw = 7 (no label)

        var result = DidValueDecoder.Decode(payload, new[] { field });

        // 表中无 7 → 回退显示 raw,带提示。
        result[0].PhysicalValue.Should().Contain("7");
    }

    [Fact]
    public void Decode_ByteField_ReturnsHex()
    {
        var field = Field("DataRecord", 16, 0, DidBaseType.ByteField);
        var payload = new byte[] { 0xDE, 0xAD };

        var result = DidValueDecoder.Decode(payload, new[] { field });

        result[0].PhysicalValue.Should().Be("DE AD");
    }

    [Fact]
    public void Decode_MultiFieldComposite_PreservesOffsets()
    {
        // 模拟复合 DID: offset 0 = 1 字节枚举 flag, offset 3 = 2 字节温度
        var table = new Dictionary<long, string> { [0] = "Init", [1] = "Run" };
        var flag = Field("Flag", 8, 0, DidBaseType.UInt32,
            CompuMethod.TexttableOf(table));
        var unit = new DidUnit("_C", "°C");
        var temp = Field("Temp", 16, 3, DidBaseType.UInt32,
            CompuMethod.LinearOf(0.5, -40.0), unit);
        // bytes: [0]=flag=1(Run), [1..2]保留填充, [3..4]=0x00C8=200→60°C
        var payload = new byte[] { 0x01, 0x00, 0x00, 0x00, 0xC8 };

        var result = DidValueDecoder.Decode(payload, new[] { flag, temp });

        result.Should().HaveCount(2);
        result[0].PhysicalValue.Should().Be("Run");
        result[1].PhysicalValue.Should().Be("60°C");
    }

    [Fact]
    public void Decode_PayloadShorterThanField_SafeFallback()
    {
        var field = Field("Big", 32, 0, DidBaseType.UInt32); // 4 字节
        var payload = new byte[] { 0x01, 0x02 }; // 只 2 字节

        var action = () => DidValueDecoder.Decode(payload, new[] { field });

        action.Should().NotThrow("短 payload 不应致命,应作 raw 部分截取");
    }

    [Fact]
    public void Decode_NoFields_PreservesRawHex()
    {
        // 无字段类型表 → 退化为整体 hex 展示。
        var payload = new byte[] { 0x01, 0x02, 0x03 };

        var result = DidValueDecoder.Decode(payload, Array.Empty<DidField>());

        result.Should().ContainSingle().Which.PhysicalValue.Should().Be("01 02 03");
    }
}
