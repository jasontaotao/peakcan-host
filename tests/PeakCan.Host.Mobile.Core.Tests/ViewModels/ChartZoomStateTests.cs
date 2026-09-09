using FluentAssertions;
using PeakCan.Host.Mobile.Core.ViewModels;
using Xunit;

namespace PeakCan.Host.Mobile.Core.Tests.ViewModels;

public class ChartZoomStateTests
{
    [Fact]
    public void Capture_Stores_Finite_Increasing_Range()
    {
        var state = new ChartZoomState();

        state.Capture(4, 6).Should().BeTrue();

        state.TryGet(out var range).Should().BeTrue();
        range.Should().Be(new ChartAxisRange(4, 6));
    }

    [Theory]
    [InlineData(null, 6.0)]
    [InlineData(4.0, null)]
    [InlineData(6.0, 4.0)]
    [InlineData(double.NaN, 6.0)]
    [InlineData(4.0, double.PositiveInfinity)]
    public void Capture_Ignores_Invalid_Range(double? minimum, double? maximum)
    {
        var state = new ChartZoomState();

        state.Capture(minimum, maximum).Should().BeFalse();
        state.TryGet(out _).Should().BeFalse();
    }

    [Fact]
    public void Reset_Removes_Captured_Range()
    {
        var state = new ChartZoomState();
        state.Capture(4, 6);

        state.Reset();

        state.TryGet(out _).Should().BeFalse();
    }
}
