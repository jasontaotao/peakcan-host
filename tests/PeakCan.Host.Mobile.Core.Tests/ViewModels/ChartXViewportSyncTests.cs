using FluentAssertions;
using PeakCan.Host.Mobile.Core.ViewModels;
using Xunit;

namespace PeakCan.Host.Mobile.Core.Tests.ViewModels;

public class ChartXViewportSyncTests
{
    private sealed class FakeAxis : IChartXAxisViewport
    {
        public ChartAxisRange? Range { get; private set; }

        public bool TryGetRange(out ChartAxisRange range)
        {
            if (Range is { } stored)
            {
                range = stored;
                return true;
            }

            range = default;
            return false;
        }

        public void SetRange(ChartAxisRange range) => Range = range;
    }

    [Fact]
    public void SyncFrom_Propagates_Source_Range_To_All_Axes()
    {
        var sync = new ChartXViewportSync();
        var first = new FakeAxis();
        var second = new FakeAxis();
        var third = new FakeAxis();
        sync.Attach([first, second, third]);
        first.SetRange(new ChartAxisRange(4, 6));

        sync.SyncFrom(first).Should().BeTrue();

        second.Range.Should().Be(new ChartAxisRange(4, 6));
        third.Range.Should().Be(new ChartAxisRange(4, 6));
        sync.Range.Should().Be(new ChartAxisRange(4, 6));
    }

    [Fact]
    public void Attach_Applies_Stored_Range_To_New_Axes()
    {
        var sync = new ChartXViewportSync();
        var original = new FakeAxis();
        sync.Attach([original]);
        original.SetRange(new ChartAxisRange(1, 2));
        sync.SyncFrom(original).Should().BeTrue();

        var replacement = new FakeAxis();
        sync.Clear();
        sync.Attach([replacement]);

        replacement.Range.Should().Be(new ChartAxisRange(1, 2));
    }

    [Fact]
    public void Reset_Removes_Stored_Range()
    {
        var sync = new ChartXViewportSync();
        var axis = new FakeAxis();
        sync.Attach([axis]);
        axis.SetRange(new ChartAxisRange(1, 2));
        sync.SyncFrom(axis).Should().BeTrue();

        sync.Reset();

        sync.Range.Should().BeNull();
    }

    [Theory]
    [InlineData(6, 4)]
    [InlineData(double.NaN, 4)]
    [InlineData(4, double.PositiveInfinity)]
    public void SyncFrom_Ignores_Invalid_Range(double minimum, double maximum)
    {
        var sync = new ChartXViewportSync();
        var axis = new FakeAxis();
        axis.SetRange(new ChartAxisRange(minimum, maximum));
        sync.Attach([axis]);

        sync.SyncFrom(axis).Should().BeFalse();
        sync.Range.Should().BeNull();
    }
}
