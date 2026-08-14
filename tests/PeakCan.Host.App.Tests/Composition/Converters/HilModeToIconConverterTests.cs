using System.Globalization;
using FluentAssertions;
using PeakCan.Host.App.Composition.Converters;
using PeakCan.Host.App.Composition.Icons;
using PeakCan.HIL.Core.HIL;
using Xunit;

namespace PeakCan.Host.App.Tests.Composition.Converters;

/// <summary>
/// HilModeToIconConverter 单元测试: HilMode enum -> Segoe Fluent Icons 码点字符串.
/// 期望值直接引用 FluentIconGlyphs 常量（与码点定义保持同步）。
/// null-safe unboxing, 未知值返回 Help 码点.
/// </summary>
public sealed class HilModeToIconConverterTests
{
    private readonly HilModeToIconConverter _converter = new();

    [Theory]
    [InlineData(HilMode.TraceReplay, FluentIconGlyphs.Replay)]
    [InlineData(HilMode.Hardware, FluentIconGlyphs.Plug)]
    [InlineData(HilMode.VirtualEcu, FluentIconGlyphs.Laptop)]
    [InlineData(HilMode.Matrix, FluentIconGlyphs.Link)]
    public void Convert_KnownMode_Returns_CorrectGlyph(HilMode mode, string expected)
    {
        var result = _converter.Convert(mode, typeof(string), null, CultureInfo.InvariantCulture);
        result.Should().Be(expected);
    }

    [Fact]
    public void Convert_Null_Returns_Help()
    {
        var result = _converter.Convert(null, typeof(string), null, CultureInfo.InvariantCulture);
        result.Should().Be(FluentIconGlyphs.Help);
    }

    [Fact]
    public void Convert_UnknownEnumValue_Returns_Help()
    {
        var result = _converter.Convert((HilMode)999, typeof(string), null, CultureInfo.InvariantCulture);
        result.Should().Be(FluentIconGlyphs.Help);
    }
}
