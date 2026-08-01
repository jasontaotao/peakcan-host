using System.Globalization;
using FluentAssertions;
using PeakCan.Host.App.Composition.Converters;
using PeakCan.Host.Core.HIL;
using Xunit;

namespace PeakCan.Host.App.Tests.Composition.Converters;

/// <summary>
/// HilModeToDescriptionConverter 单元测试: HilMode enum -> 中文功能说明.
/// null-safe unboxing, 未知值返回空串.
/// </summary>
public sealed class HilModeToDescriptionConverterTests
{
    private readonly HilModeToDescriptionConverter _converter = new();

    [Theory]
    [InlineData(HilMode.TraceReplay)]
    [InlineData(HilMode.Hardware)]
    [InlineData(HilMode.VirtualEcu)]
    [InlineData(HilMode.Matrix)]
    public void Convert_KnownMode_Returns_NonEmptyChineseDescription(HilMode mode)
    {
        var result = _converter.Convert(mode, typeof(string), null, CultureInfo.InvariantCulture);
        result.Should().BeOfType<string>().Which.Should().NotBeEmpty();
    }

    [Fact]
    public void Convert_Null_Returns_EmptyString()
    {
        var result = _converter.Convert(null, typeof(string), null, CultureInfo.InvariantCulture);
        result.Should().Be("");
    }

    [Fact]
    public void Convert_UnknownEnumValue_Returns_EmptyString()
    {
        var result = _converter.Convert((HilMode)999, typeof(string), null, CultureInfo.InvariantCulture);
        result.Should().Be("");
    }
}
