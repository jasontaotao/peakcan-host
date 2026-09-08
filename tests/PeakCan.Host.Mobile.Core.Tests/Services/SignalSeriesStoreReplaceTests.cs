using FluentAssertions;
using PeakCan.Host.Mobile.Core.Services;
using Xunit;

namespace PeakCan.Host.Mobile.Core.Tests.Services;

public class SignalSeriesStoreReplaceTests
{
    [Fact]
    public void ReplaceSamples_Clears_And_Stores_Timestamp_Ordered_Samples()
    {
        var store = new SignalSeriesStore(capacity: 10);
        store.Add(20, 2);
        store.Add(1, 1);

        store.ReplaceSamples(
        [
            new(30, 3),
            new(10, 0),
            new(20, 2),
            new(double.NaN, 9),
        ]);

        store.Count.Should().Be(3);
        var points = store.GetRenderPoints(10);
        points.Select(p => p.Timestamp).Should().Equal(10, 20, 30);
    }

    [Fact]
    public void ReplaceSamples_Maintains_Capacity()
    {
        var store = new SignalSeriesStore(capacity: 3);
        store.ReplaceSamples(
        [
            new(4, 4),
            new(1, 1),
            new(3, 3),
            new(2, 2),
        ]);

        store.Count.Should().Be(3);
        store.GetRenderPoints(10).Select(p => p.Timestamp).Should().Equal(2, 3, 4);
    }
}
