using System.Globalization;
using FluentAssertions;
using PeakCan.Host.App.Composition.Converters;
using Xunit;

namespace PeakCan.Host.App.Tests.Composition.Converters;

/// <summary>
/// HexConverter 单元测试: 验证双向 hex 解析.
/// 解决 StringFormat=0x{0:X4} 在双向绑定时无法解析回源的 bug
/// (operator 编辑 RoutineId 后切换 step, 值被重置).
/// </summary>
public sealed class HexConverterTests
{
    private readonly HexConverter _converter = new();

    [Theory]
    [InlineData((ushort)0x0204, 4, "0x0204")]
    [InlineData((ushort)0xFF, 4, "0x00FF")]
    [InlineData((ushort)0x0, 4, "0x0000")]
    [InlineData(0x0800_0000u, 8, "0x08000000")]
    [InlineData((byte)0x01, 2, "0x01")]
    public void Convert_Formats_With_0x_Prefix_And_Width(object value, int width, string expected)
    {
        var result = _converter.Convert(value, typeof(string), width, CultureInfo.InvariantCulture);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("0x0204", typeof(ushort), 4, (ushort)0x0204)]
    [InlineData("0204", typeof(ushort), 4, (ushort)0x0204)]
    [InlineData("0X0204", typeof(ushort), 4, (ushort)0x0204)]  // uppercase prefix
    [InlineData(" 0x0204 ", typeof(ushort), 4, (ushort)0x0204)]  // surrounding whitespace
    [InlineData("0xABCDEF", typeof(uint), 4, 0xABCDEFu)]
    [InlineData("0x08000000", typeof(uint), 8, 0x08000000u)]
    [InlineData("08000000", typeof(uint), 8, 0x08000000u)]  // no prefix
    [InlineData("0x01", typeof(byte), 2, (byte)0x01)]
    public void ConvertBack_Parses_Hex_With_Optional_0x_Prefix(string input, Type targetType, int width, object expected)
    {
        var result = _converter.ConvertBack(input, targetType, width, CultureInfo.InvariantCulture);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("not-hex", typeof(ushort))]
    [InlineData("0xZZZZ", typeof(ushort))]
    [InlineData("", typeof(ushort))]
    public void ConvertBack_Invalid_Input_Returns_Unset(string input, Type targetType)
    {
        // Unset means the binding does NOT write back to source - preserves the old value
        // instead of corrupting it with a parse failure.
        var result = _converter.ConvertBack(input, targetType, 4, CultureInfo.InvariantCulture);
        result.Should().Be(System.Windows.DependencyProperty.UnsetValue);
    }

    [Fact]
    public void ConvertBack_Null_Input_Returns_Unset()
    {
        var result = _converter.ConvertBack(null, typeof(ushort), 4, CultureInfo.InvariantCulture);
        result.Should().Be(System.Windows.DependencyProperty.UnsetValue);
    }
}
