using FluentAssertions;
using PeakCan.Host.Mobile.Core.Services;

namespace PeakCan.Host.Mobile.Core.Tests.Services;

public class ChartRenderFilterTests
{
    [Fact]
    public void ClampToViewport_Filters_NonFinite_And_Outside_Window()
    {
        var points = new[]
        {
            new ChartPoint(0, 1),
            new ChartPoint(1, double.NaN),
            new ChartPoint(2, 2),
            new ChartPoint(3, 3),
            new ChartPoint(4, double.NaN),
        };

        var result = ChartRenderFilter.ClampToViewport(points, 1, 3);

        result.Should().Contain(p => p.Timestamp == 2 && p.Value == 2);
        result.Should().Contain(p => p.Timestamp == 3 && p.Value == 3);
        result.Should().NotContain(p => !double.IsFinite(p.Value));
    }

    [Theory]
    [InlineData(double.NegativeInfinity, double.PositiveInfinity)]
    [InlineData(double.NaN, double.PositiveInfinity)]
    [InlineData(5, 5)]
    public void ClampToViewport_Returns_Input_For_Invalid_Window(double minimum, double maximum)
    {
        var points = new[] { new ChartPoint(1, 1), new ChartPoint(2, 2) };

        var result = ChartRenderFilter.ClampToViewport(points, minimum, maximum);

        result.Should().BeSameAs(points);
    }
}
