using System.Globalization;
using FluentAssertions;
using PeakCan.Host.App.Converters;
using Xunit;

namespace PeakCan.Host.App.Tests.Converters;

/// <summary>
/// NM 能力 chip 的背景色转换器：可用/缺失必须可区分，非布尔值不能落到「可用」。
/// </summary>
public class NmChipBrushConverterTests
{
    private readonly NmChipBrushConverter _sut = new();

    private object Map(object? value)
        => _sut.Convert(value, typeof(object), null, CultureInfo.InvariantCulture);

    [Fact]
    public void AvailableAndMissing_UseDistinctBrushes()
    {
        Map(true).Should().NotBeSameAs(Map(false));
    }

    [Fact]
    public void NonBooleanValue_FallsBackToMissingBrush()
    {
        var missing = Map(false);

        Map(null).Should().BeSameAs(missing);
        Map("true").Should().BeSameAs(missing,
            "只有真正的 bool true 才能显示为可用；字符串不能冒充能力可用");
    }
}
