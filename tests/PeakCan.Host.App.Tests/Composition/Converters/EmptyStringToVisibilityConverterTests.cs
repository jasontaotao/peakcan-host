using System.Globalization;
using System.Windows;
using FluentAssertions;
using PeakCan.Host.App.Composition.Converters;
using Xunit;

namespace PeakCan.Host.App.Tests.Composition.Converters;

/// <summary>
/// EmptyStringToVisibilityConverter 单元测试: null/空串 -> Visible, 非空 -> Collapsed.
/// 用于 HIL Browse 字段占位符 overlay 的显示控制.
/// </summary>
public sealed class EmptyStringToVisibilityConverterTests
{
    private readonly EmptyStringToVisibilityConverter _converter = new();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Convert_NullOrEmpty_Returns_Visible(string? value)
    {
        var result = _converter.Convert(value, typeof(Visibility), null, CultureInfo.InvariantCulture);
        result.Should().Be(Visibility.Visible);
    }

    [Theory]
    [InlineData(" ")]              // 空格 -> Collapsed（与 NullToVisibilityConverter 惯例一致）
    [InlineData("hello")]
    [InlineData("/path/to/file.dbc")]
    public void Convert_NonEmpty_Returns_Collapsed(string value)
    {
        var result = _converter.Convert(value, typeof(Visibility), null, CultureInfo.InvariantCulture);
        result.Should().Be(Visibility.Collapsed);
    }

    [Fact]
    public void ConvertBack_Throws_NotSupportedException()
    {
        var act = () => _converter.ConvertBack(
            Visibility.Visible, typeof(string), null, CultureInfo.InvariantCulture);
        act.Should().Throw<NotSupportedException>();
    }
}
