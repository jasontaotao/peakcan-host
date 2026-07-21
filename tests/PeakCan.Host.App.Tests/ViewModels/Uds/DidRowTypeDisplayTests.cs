using System.Collections.Generic;
using FluentAssertions;
using PeakCan.Host.App.ViewModels.Uds;
using PeakCan.Host.App.ViewModels.Uds.Rows;
using PeakCan.Host.Core.Uds.Odx;

namespace PeakCan.Host.App.Tests.ViewModels.Uds;

/// <summary>
/// v3.49.0 MINOR T4.1 — <see cref="DidRow"/> 字段类型展示与解码结果
/// 绑定字段的单元测试。
/// </summary>
public class DidRowTypeDisplayTests
{
    [Fact]
    public void TypeDisplay_NoFields_ShowsRaw()
    {
        var row = new DidRow
        {
            Id = 0xF190, Name = "VIN", LengthBytes = 0, Writable = false,
        };

        // 无字段类型表 → "(no type)" 标识,与既有 length=0 行为兼容。
        row.TypeDisplay.Should().Be("(no type)");
    }

    [Fact]
    public void TypeDisplay_SingleScalarField_ShowsTypeName()
    {
        var row = new DidRow
        {
            Id = 0xF401, Name = "Temp", LengthBytes = 2, Writable = false,
        };
        row.SetFields(new[]
        {
            new DidField("Temp", 16, 0, DidBaseType.UInt32,
                CompuMethod.LinearOf(0.5, -40.0), new DidUnit("_C", "°C")),
        });

        row.TypeDisplay.Should().Be("UInt32[16]");
    }

    [Fact]
    public void TypeDisplay_AsciiString_ShowsTypeNameWithBytes()
    {
        var row = new DidRow
        {
            Id = 0xF190, Name = "VIN", LengthBytes = 17, Writable = false,
        };
        row.SetFields(new[]
        {
            new DidField("VIN", 17 * 8, 0, DidBaseType.AsciiString, null, null),
        });

        row.TypeDisplay.Should().Be("AsciiString[17B]");
    }

    [Fact]
    public void TypeDisplay_CompositeMultiField_ShowsCount()
    {
        // 复合 DID (e.g. CellVolt 8 字段) → "ByteField ×8"
        var row = new DidRow
        {
            Id = 0x0102, Name = "CellVolt", LengthBytes = 400, Writable = false,
        };
        var fields = new List<DidField>();
        for (int i = 0; i < 8; i++)
            fields.Add(new DidField($"f{i}", i == 0 ? 1456 : 48,
                3 + i * 6, DidBaseType.ByteField, null, null));
        row.SetFields(fields);

        row.TypeDisplay.Should().Be("ByteField ×8");
    }

    [Fact]
    public void DecodedFields_CanBeSet_AndPopulatesSummary()
    {
        var row = new DidRow
        {
            Id = 0xF401, Name = "Temp", LengthBytes = 2, Writable = false,
        };
        row.SetFields(new[]
        {
            new DidField("Temp", 16, 0, DidBaseType.UInt32,
                CompuMethod.LinearOf(0.5, -40.0), new DidUnit("_C", "°C")),
        });

        // 模拟 ReadDid 命中后调用解码:
        row.SetDecoded(new[]
        {
            new DecodedField("Temp", "0x0064", "10°C", "°C"),
        });

        row.DecodedFields.Should().ContainSingle();
        row.DecodedFields[0].PhysicalValue.Should().Be("10°C");
        row.DecodedSummary.Should().Contain("10°C");
    }
}
