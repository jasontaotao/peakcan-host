using FluentAssertions;
using OxyPlot;
using PeakCan.Host.App.ViewModels;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

public class TraceChartViewModelTests
{
    private static TraceChartSeries MakeSeries(string key, params (double x, double y)[] pts)
    {
        var plot = new PlotModel();
        plot.Axes.Add(new OxyPlot.Axes.LinearAxis { Position = OxyPlot.Axes.AxisPosition.Bottom });
        plot.Axes.Add(new OxyPlot.Axes.LinearAxis { Position = OxyPlot.Axes.AxisPosition.Left });
        var line = new OxyPlot.Series.LineSeries { Title = key };
        foreach (var (x, y) in pts) line.Points.Add(new DataPoint(x, y));
        plot.Series.Add(line);
        var xs = pts.Select(p => p.x).ToArray();
        var ys = pts.Select(p => p.y).ToArray();
        // Handle the no-points case (used by AddSeries/RemoveSeries/
        // ToggleCollapse/SetFocus tests) so the Min/Max record fields are
        // always well-defined. The contract tests assert on observable
        // behavior, not on these scalar values.
        var min = ys.Length == 0 ? 0.0 : ys.Min();
        var max = ys.Length == 0 ? 0.0 : ys.Max();
        return new TraceChartSeries(key, key, "", OxyColors.Blue, plot, xs, ys,
            min, max, false, false);
    }

    [Fact]
    public void Ctor_Empty_HasZeroSeries()
    {
        var sut = new TraceChartViewModel();
        sut.Series.Should().BeEmpty();
        sut.PlaybackCursorX.Should().Be(0.0);
        sut.TotalDuration.Should().Be(0.0);
    }

    [Fact]
    public void AddSeries_AppendsToObservableCollection()
    {
        var sut = new TraceChartViewModel();
        sut.AddSeries(MakeSeries("A"));
        sut.AddSeries(MakeSeries("B"));
        sut.Series.Should().HaveCount(2);
    }

    [Fact]
    public void RemoveSeries_RemovesFromCollection()
    {
        var sut = new TraceChartViewModel();
        var s = MakeSeries("A");
        sut.AddSeries(s);
        sut.RemoveSeries(s);
        sut.Series.Should().BeEmpty();
    }

    [Fact]
    public void UpdatePlaybackCursor_SetsProperty()
    {
        var sut = new TraceChartViewModel();
        sut.UpdatePlaybackCursor(12.345);
        sut.PlaybackCursorX.Should().Be(12.345);
    }

    [Fact]
    public void GetStatistics_ReturnsMinMaxAvgN()
    {
        var sut = new TraceChartViewModel();
        sut.AddSeries(MakeSeries("A", (0, 10), (1, 20), (2, 30)));
        var stats = sut.GetStatistics().ToList();
        stats.Should().HaveCount(1);
        stats[0].SignalKey.Should().Be("A");
        stats[0].Min.Should().Be(10);
        stats[0].Max.Should().Be(30);
        stats[0].Average.Should().Be(20);
        stats[0].SampleCount.Should().Be(3);
    }

    [Fact]
    public void ToggleCollapse_FlapsIsCollapsed()
    {
        var sut = new TraceChartViewModel();
        var s = MakeSeries("A");
        sut.AddSeries(s);
        sut.ToggleCollapse(s);
        sut.Series.First().IsCollapsed.Should().BeTrue();
        sut.ToggleCollapse(s);
        sut.Series.First().IsCollapsed.Should().BeFalse();
    }

    [Fact]
    public void SetFocus_TogglesOtherSeriesFocusedFalse()
    {
        var sut = new TraceChartViewModel();
        var a = MakeSeries("A");
        var b = MakeSeries("B");
        sut.AddSeries(a);
        sut.AddSeries(b);
        sut.SetFocus(a);
        sut.Series.First(x => x.SignalKey == "A").IsFocused.Should().BeTrue();
        sut.Series.First(x => x.SignalKey == "B").IsFocused.Should().BeFalse();
        sut.SetFocus(b);
        sut.Series.First(x => x.SignalKey == "A").IsFocused.Should().BeFalse();
        sut.Series.First(x => x.SignalKey == "B").IsFocused.Should().BeTrue();
    }
}
