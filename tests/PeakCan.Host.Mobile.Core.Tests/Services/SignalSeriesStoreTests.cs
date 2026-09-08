using FluentAssertions;
using PeakCan.Host.Mobile.Core.Services;
using Xunit;

namespace PeakCan.Host.Mobile.Core.Tests.Services;

public class SignalSeriesStoreTests
{
    [Fact]
    public void Add_Appends_Timestamp_Ordered_Samples()
    {
        var store = new SignalSeriesStore(capacity: 10);
        store.Add(1, 10);
        store.Add(2, 20);

        store.Count.Should().Be(2);
        store.GetRenderPoints(4).Should().HaveCount(2);
    }

    [Fact]
    public void Add_Thins_History_When_Capacity_Exceeded()
    {
        var store = new SignalSeriesStore(capacity: 3);
        store.Add(1, 1);
        store.Add(2, 2);
        store.Add(3, 3);
        store.Add(4, 4);

        store.Count.Should().BeLessThanOrEqualTo(3);
        var points = store.GetRenderPoints(100);
        points.Should().Contain(p => p.Timestamp == 1);
        points.Should().Contain(p => p.Timestamp == 4);
    }

    [Fact]
    public void GetRenderPoints_Creates_Min_Max_Per_Bucket()
    {
        var store = new SignalSeriesStore(capacity: 10);
        store.Add(0, 1);
        store.Add(1, 100);
        store.Add(2, 2);
        store.Add(3, 50);
        store.Add(4, 5);

        var points = store.GetRenderPoints(2);

        points.Should().HaveCount(4);
        points[0].Timestamp.Should().Be(0);
        points[0].Value.Should().Be(1);
        points[1].Timestamp.Should().Be(1);
        points[1].Value.Should().Be(100);
        points[2].Timestamp.Should().Be(2);
        points[2].Value.Should().Be(2);
        points[3].Timestamp.Should().Be(3);
        points[3].Value.Should().Be(50);
    }

    [Fact]
    public void GetRenderPoints_Emits_Min_Max_In_Timestamp_Order()
    {
        var store = new SignalSeriesStore(capacity: 4);
        store.Add(0, 10);
        store.Add(1, 0);

        var points = store.GetRenderPoints(1);

        points.Should().HaveCount(2);
        points[0].Timestamp.Should().Be(0);
        points[0].Value.Should().Be(10);
        points[1].Timestamp.Should().Be(1);
        points[1].Value.Should().Be(0);
    }

    [Fact]
    public void GetRenderPoints_Ignores_Outside_Time_Range()
    {
        var store = new SignalSeriesStore(capacity: 10);
        store.Add(0, 1);
        store.Add(10, 100);
        store.Add(20, 5);
        store.Add(30, 50);

        var points = store.GetRenderPoints(10, 20, 4);

        points.Select(p => p.Timestamp).Should().OnlyContain(t => t >= 10 && t <= 20);
        points.Should().HaveCount(2);
    }

    [Fact]
    public void GetRenderPoints_Handles_Degenerate_Range()
    {
        var store = new SignalSeriesStore(capacity: 10);
        store.Add(5, 1);
        store.Add(5, 3);

        store.GetRenderPoints(1).Should().HaveCount(2);
        store.GetRenderPoints(5, 5, 3).Should().HaveCount(2);
        new SignalSeriesStore().GetRenderPoints(8).Should().BeEmpty();
    }

    [Fact]
    public void Add_Ignores_Non_Finite_Values()
    {
        var store = new SignalSeriesStore(capacity: 4);
        store.Add(1, double.NaN);
        store.Add(2, double.PositiveInfinity);
        store.Add(double.NaN, 3);
        store.Add(4, 4);

        store.Count.Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_Add_And_Read_Maintains_Capacity()
    {
        var store = new SignalSeriesStore(capacity: 3_000);
        const int perTask = 2_000;
        var start = new ManualResetEventSlim(false);

        var task1 = Task.Run(async () =>
        {
            start.Wait();
            for (var i = 0; i < perTask; i++) store.Add(i, i);
        });
        var task2 = Task.Run(async () =>
        {
            start.Wait();
            for (var i = 0; i < perTask; i++) store.Add(i + 100_000, i);
        });

        start.Set();
        await Task.WhenAll(task1, task2);

        store.Count.Should().BeLessThanOrEqualTo(3_000);
        var points = store.GetRenderPoints(10);
        points.Should().NotBeEmpty();
        points.Should().Contain(p => p.Timestamp == 0);
        points.Should().Contain(p => p.Timestamp >= 100_000);
            }

    [Fact]
    public void Clear_Removes_All_Samples()
    {
        var store = new SignalSeriesStore(capacity: 4);
        store.Add(1, 1);
        store.Add(2, 2);

        store.Clear();

        store.Count.Should().Be(0);
        store.GetRenderPoints(4).Should().BeEmpty();
    }
}







