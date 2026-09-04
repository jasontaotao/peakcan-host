using FluentAssertions;
using NSubstitute;
using ScottPlot;
using ScottPlot.Interactivity;
using ScottPlot.Interactivity.UserActions;
using PeakCan.Host.App.Services.Trace;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.Trace;

public class SharedXAxisSyncResponseTests
{
    private static (IPlotControl Control, Plot Plot) MakeControl()
    {
        var plot = new Plot();
        plot.Axes.SetLimitsX(0, 100);
        var control = Substitute.For<IPlotControl>();
        control.Plot.Returns(plot);
        return (control, plot);
    }

    private static List<(double Min, double Max, string ExcludeKey)> MakeLog()
        => new();

    [Fact]
    public void Execute_MouseWheelUp_BroadcastsImmediately()
    {
        var (control, plot) = MakeControl();
        plot.Axes.SetLimitsX(10, 20);
        var log = MakeLog();
        var response = new SharedXAxisSyncResponse("A", (min, max, key) => log.Add((min, max, key)), () => true);

        response.Execute(control, new MouseWheelUp(new Pixel()), new KeyboardState());

        log.Should().ContainSingle().Which.Should().Be((10d, 20d, "A"));
    }

    [Fact]
    public void Execute_LeftMouseDown_StartsDragTrackingWithoutBroadcast()
    {
        var (control, plot) = MakeControl();
        plot.Axes.SetLimitsX(10, 20);
        var log = MakeLog();
        var response = new SharedXAxisSyncResponse("A", (min, max, key) => log.Add((min, max, key)), () => true);

        response.Execute(control, new LeftMouseDown(new Pixel()), new KeyboardState());

        log.Should().BeEmpty();
    }

    [Fact]
    public void Execute_MouseMove_WhileDragging_ThrottlesTo40ms()
    {
        var (control, plot) = MakeControl();
        var log = MakeLog();
        var now = new DateTime(2026, 9, 4, 0, 0, 0, DateTimeKind.Utc);
        var response = new SharedXAxisSyncResponse("A", (min, max, key) => log.Add((min, max, key)), () => true, () => now);

        plot.Axes.SetLimitsX(10, 20);
        response.Execute(control, new LeftMouseDown(new Pixel()), new KeyboardState());
        response.Execute(control, new MouseMove(new Pixel()), new KeyboardState());

        now = now.AddMilliseconds(39);
        plot.Axes.SetLimitsX(11, 21);
        response.Execute(control, new MouseMove(new Pixel()), new KeyboardState());

        now = now.AddMilliseconds(41);
        plot.Axes.SetLimitsX(12, 22);
        response.Execute(control, new MouseMove(new Pixel()), new KeyboardState());

        log.Should().HaveCount(2);
        log[0].Should().Be((10d, 20d, "A"));
        log[1].Should().Be((12d, 22d, "A"));
    }

    [Fact]
    public void Execute_LeftMouseUp_BroadcastsPending()
    {
        var (control, plot) = MakeControl();
        var log = MakeLog();
        var now = new DateTime(2026, 9, 4, 0, 0, 0, DateTimeKind.Utc);
        var response = new SharedXAxisSyncResponse("A", (min, max, key) => log.Add((min, max, key)), () => true, () => now);

        plot.Axes.SetLimitsX(10, 20);
        response.Execute(control, new LeftMouseDown(new Pixel()), new KeyboardState());
        response.Execute(control, new MouseMove(new Pixel()), new KeyboardState());

        now = now.AddMilliseconds(10);
        plot.Axes.SetLimitsX(11, 21);
        response.Execute(control, new LeftMouseUp(new Pixel()), new KeyboardState());

        log.Should().HaveCount(2);
        log[^1].Should().Be((11d, 21d, "A"));
    }

    [Fact]
    public void Execute_MouseMove_WithoutDrag_NoBroadcast()
    {
        var (control, plot) = MakeControl();
        plot.Axes.SetLimitsX(10, 20);
        var log = MakeLog();
        var response = new SharedXAxisSyncResponse("A", (min, max, key) => log.Add((min, max, key)), () => true);

        response.Execute(control, new MouseMove(new Pixel()), new KeyboardState());

        log.Should().BeEmpty();
    }

    [Fact]
    public void Execute_SyncDisabled_NoBroadcast()
    {
        var (control, plot) = MakeControl();
        plot.Axes.SetLimitsX(10, 20);
        var log = MakeLog();
        var response = new SharedXAxisSyncResponse("A", (min, max, key) => log.Add((min, max, key)), () => false);

        response.Execute(control, new MouseWheelUp(new Pixel()), new KeyboardState());
        response.Execute(control, new LeftMouseDown(new Pixel()), new KeyboardState());
        response.Execute(control, new MouseMove(new Pixel()), new KeyboardState());
        response.Execute(control, new LeftMouseUp(new Pixel()), new KeyboardState());

        log.Should().BeEmpty();
    }
}
