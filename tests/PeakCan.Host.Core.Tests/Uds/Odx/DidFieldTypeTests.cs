using System.Collections.Generic;
using FluentAssertions;
using PeakCan.Host.Core.Uds.Odx;

namespace PeakCan.Host.Core.Tests.Uds.Odx;

/// <summary>
/// v3.49.0 MINOR — DID 数据类型识别 T0.1 类型模型的单元测试。
/// 验证 <see cref="DidBaseType"/> / <see cref="CompuMethod"/> /
/// <see cref="DidUnit"/> / <see cref="DidField"/> 这组纯数据结构的行为契约：
/// record 相等性、工厂方法、文本表与单位语义。
/// </summary>
public class DidFieldTypeTests
{
    [Fact]
    public void CompuMethod_Identical_HasNoScaling()
    {
        var m = CompuMethod.Identical;
        m.Category.Should().Be(CompuCategory.Identical);
        m.LinearA.Should().Be(1.0);
        m.LinearB.Should().Be(0.0);
        m.TextTable.Should().BeEmpty();
    }

    [Fact]
    public void CompuMethod_LinearOf_CapturesCoefficients()
    {
        // temperature: physical = 0.5 * raw - 40
        var m = CompuMethod.LinearOf(0.5, -40.0);
        m.Category.Should().Be(CompuCategory.Linear);
        m.LinearA.Should().Be(0.5);
        m.LinearB.Should().Be(-40.0);
        m.TextTable.Should().BeEmpty();
    }

    [Fact]
    public void CompuMethod_TexttableOf_HoldsRawToLabelMap()
    {
        var table = new Dictionary<long, string>
        {
            [0] = "Off",
            [1] = "On",
            [2] = "Fault",
        };
        var m = CompuMethod.TexttableOf(table);
        m.Category.Should().Be(CompuCategory.Texttable);
        m.TextTable[0].Should().Be("Off");
        m.TextTable[1].Should().Be("On");
        m.TextTable[2].Should().Be("Fault");
        m.TextTable.Should().HaveCount(3);
    }

    [Fact]
    public void Unit_Empty_IsMarkedEmpty()
    {
        var u = DidUnit.Empty;
        u.IsEmpty.Should().BeTrue("no SHORT-NAME means no unit");
        u.ShortName.Should().BeEmpty();
        u.DisplayName.Should().BeEmpty();
    }

    [Fact]
    public void Unit_WithShortName_IsNotEmpty()
    {
        var u = new DidUnit("C", "degC");
        u.IsEmpty.Should().BeFalse();
        u.ShortName.Should().Be("C");
        u.DisplayName.Should().Be("degC");
    }

    [Fact]
    public void DidField_RecordEquality_DistinguishesByAllMembers()
    {
        var unit = new DidUnit("C", "degC");
        var compu = CompuMethod.LinearOf(0.5, -40.0);

        var a = new DidField("Temp", 16, 0, DidBaseType.UInt32, compu, unit);
        var b = new DidField("Temp", 16, 0, DidBaseType.UInt32, compu, unit);
        var diff = new DidField("Temp", 16, 0, DidBaseType.UInt32, compu, null);

        a.Should().Be(b, "all members match");
        a.GetHashCode().Should().Be(b.GetHashCode());
        a.Should().NotBe(diff, "DidUnit differs");
    }

    [Fact]
    public void DidField_AllowsNullCompuAndUnit_ForScalarDidsWithoutMetadata()
    {
        var f = new DidField("RawCounter", 16, 0, DidBaseType.UInt32, null, null);
        f.Compu.Should().BeNull();
        f.Unit.Should().BeNull();
        f.BaseType.Should().Be(DidBaseType.UInt32);
    }
}
