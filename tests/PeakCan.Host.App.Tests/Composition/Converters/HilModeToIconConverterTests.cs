using System.Globalization;
using FluentAssertions;
using PeakCan.Host.App.Composition.Converters;
using PeakCan.Host.Core.HIL;
using Xunit;

namespace PeakCan.Host.App.Tests.Composition.Converters;

/// <summary>
/// HilModeToIconConverter 单元测试: HilMode enum -> emoji 字符串.
/// null-safe unboxing, 未知值返回 "❓".
/// </summary>
public sealed class HilModeToIconConverterTests
{
    private readonly HilModeToIconConverter _converter = new();

    [Theory]
    [InlineData(HilMode.TraceReplay, "📼")]
    [InlineData(HilMode.Hardware, "🔌")]
    [InlineData(HilMode.VirtualEcu, "💻")]
    [InlineData(HilMode.Matrix, "🔗")]
    public void Convert_KnownMode_Returns_CorrectEmoji(HilMode mode, string expected)
    {
        var result = _converter.Convert(mode, typeof(string), null, CultureInfo.InvariantCulture);
        result.Should().Be(expected);
    }

    [Fact]
    public void Convert_Null_Returns_QuestionMark()
    {
        var result = _converter.Convert(null, typeof(string), null, CultureInfo.InvariantCulture);
        result.Should().Be("❓");
    }

    [Fact]
    public void Convert_UnknownEnumValue_Returns_QuestionMark()
    {
        var result = _converter.Convert((HilMode)999, typeof(string), null, CultureInfo.InvariantCulture);
        result.Should().Be("❓");
    }
}
