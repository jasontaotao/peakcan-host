using FluentAssertions;
using PeakCan.Host.Mobile.Core.ViewModels;
using Xunit;

namespace PeakCan.Host.Mobile.Core.Tests.ViewModels;

public class ChartViewportLimitsTests
{
    [Fact]
    public void ClampToMinimumSpan_Expands_Viewport_At_Maximum_Zoom()
    {
        var requested = new ChartAxisRange(9.995, 10.005);
        var full = new ChartAxisRange(0, 10);

        var result = ChartViewportLimits.ClampToMinimumSpan(requested, full, 32);

        result.Minimum.Should().Be(9.6875);
        result.Maximum.Should().Be(10.0);
    }

    [Fact]
    public void ClampToMinimumSpan_Keeps_Range_That_Is_Not_Too_Narrow()
    {
        var requested = new ChartAxisRange(2, 4);
        var full = new ChartAxisRange(0, 10);

        var result = ChartViewportLimits.ClampToMinimumSpan(requested, full, 32);

        result.Should().Be(requested);
    }

    [Fact]
    public void ClampToAllowedSpan_Contracts_Viewport_To_Full_Range()
    {
        var requested = new ChartAxisRange(-100, 120);
        var full = new ChartAxisRange(0, 10);

        var result = ChartViewportLimits.ClampToAllowedSpan(requested, full, 32);

        result.Should().Be(full);
    }

    [Fact]
    public void ClampToAllowedSpan_Keeps_Range_Inside_Zoom_Limits()
    {
        var requested = new ChartAxisRange(2, 4);
        var full = new ChartAxisRange(0, 10);

        var result = ChartViewportLimits.ClampToAllowedSpan(requested, full, 32);

        result.Should().Be(requested);
    }

    [Fact]
    public void ClampToAllowedSpan_Expands_At_Maximum_Zoom()
    {
        var requested = new ChartAxisRange(9.995, 10.005);
        var full = new ChartAxisRange(0, 10);

        var result = ChartViewportLimits.ClampToAllowedSpan(requested, full, 32);

        result.Minimum.Should().Be(9.6875);
        result.Maximum.Should().Be(10.0);
    }

    [Theory]
    [InlineData(0, 10, double.NaN, double.PositiveInfinity, 32)]
    [InlineData(double.NaN, double.PositiveInfinity, 0, 10, 32)]
    [InlineData(5, 5, 0, 10, 32)]
    [InlineData(9.995, 10.005, 0, 10, 1)]
    public void ClampToMinimumSpan_Returns_Requested_For_Invalid_Input(
        double requestedMinimum,
        double requestedMaximum,
        double fullMinimum,
        double fullMaximum,
        double maxZoomFactor)
    {
        var requested = new ChartAxisRange(requestedMinimum, requestedMaximum);
        var full = new ChartAxisRange(fullMinimum, fullMaximum);

        var result = ChartViewportLimits.ClampToMinimumSpan(requested, full, maxZoomFactor);

        result.Should().Be(requested);
    }
}
