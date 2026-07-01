using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core;
using PeakCan.Host.Infrastructure.Statistics;

namespace PeakCan.Host.App.Tests.Services;

/// <summary>
/// Task 17: <see cref="StatisticsService"/> runs a 1 Hz timer that
/// pushes <see cref="BusStatisticsCollector.Snapshot"/> into
/// <see cref="StatsViewModel"/>. The loop mirrors the
/// <see cref="TraceService"/> pattern (Task 13) — periodic timer,
/// cancellation-aware, OCE-on-shutdown. The tests verify the
/// first-snapshot-immediate behaviour (no 1-second blank window at
/// startup) and the cancellation responsiveness.
/// <para>
/// <b>Why a separate BackgroundService?</b> the collector is a pure
/// <see cref="Infrastructure.Channel.IFrameSink"/>; the VM is bound
/// to a WPF tab. The service is the only place that crosses the
/// boundary on a fixed cadence. Tests use a stoppable <see cref="CancellationTokenSource"/>
/// to bound execution time.
/// </para>
/// </summary>
public class StatisticsServiceTests
{
    private static (StatisticsService svc, BusStatisticsCollector collector, StatsViewModel vm)
        NewWired()
    {
        var collector = new BusStatisticsCollector();
        var vm = new StatsViewModel();
        var svc = new StatisticsService(collector, vm, NullLogger<StatisticsService>.Instance);
        return (svc, collector, vm);
    }

    [Fact]
    public void Ctor_Null_Collector_Throws()
    {
        var act = () => new StatisticsService(null!, new StatsViewModel(), NullLogger<StatisticsService>.Instance);
        act.Should().Throw<ArgumentNullException>().WithParameterName("collector");
    }

    [Fact]
    public void Ctor_Null_Vm_Throws()
    {
        var act = () => new StatisticsService(new BusStatisticsCollector(), null!, NullLogger<StatisticsService>.Instance);
        act.Should().Throw<ArgumentNullException>().WithParameterName("vm");
    }

    [Fact]
    public void Ctor_Null_Logger_Throws()
    {
        var act = () => new StatisticsService(new BusStatisticsCollector(), new StatsViewModel(), null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    [Fact]
    public async Task ExecuteAsync_Takes_First_Snapshot_Immediately()
    {
        // The loop fires Push(Snapshot()) before the first 1 s tick so
        // the user sees real numbers on connect, not 60 zeros for a
        // second. The collector has no frames yet, so the first
        // snapshot's TotalFrames is 0 — but Push was called and the
        // VM's bound totals stayed in sync.
        var (svc, _, vm) = NewWired();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await svc.StartAsync(cts.Token);
        try
        {
            // The first snapshot is taken synchronously inside
            // ExecuteAsync before the loop awaits the timer; give
            // the host microsecond-scale time to schedule.
            await Task.Delay(50, cts.Token);
            vm.TotalFrames.Should().Be(0, "no frames have arrived");
            vm.FpsSeries.Should().HaveCount(60, "the rolling window stays at MaxPoints");
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ExecuteAsync_Pushes_Snapshot_After_One_Tick()
    {
        // Push a few synthetic frames into the collector, start the
        // service, wait > 1 s, expect the VM to reflect the new
        // totals. Tolerance: 2.5 s overall budget (1 s tick + slack).
        var (svc, collector, vm) = NewWired();
        for (var i = 0; i < 10; i++)
        {
            collector.OnFrame(MakeFrame());
        }
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await svc.StartAsync(cts.Token);
        try
        {
            // Wait long enough for at least one 1 s tick.
            await Task.Delay(1500, cts.Token);
            vm.TotalFrames.Should().Be(10, "the 10 frames OnFrame'd into the collector should reach the VM via the timer");
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ExecuteAsync_Stops_On_Cancellation_Within_One_Second()
    {
        var (svc, _, _) = NewWired();
        using var startCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await svc.StartAsync(startCts.Token);

        var stopTask = svc.StopAsync(CancellationToken.None);
        var completed = await Task.WhenAny(stopTask, Task.Delay(1000));
        completed.Should().BeSameAs(stopTask, "StopAsync should observe cancellation within 1 s");
        await stopTask;
    }

    private static CanFrame MakeFrame() =>
        new(new CanId(0x100, FrameFormat.Standard),
            new byte[] { 0xDE, 0xAD, 0xBE, 0xEF },
            FrameFlags.None,
            new ChannelId(0x51),
            Timestamp.FromMicroseconds(1_000_000UL));
}
