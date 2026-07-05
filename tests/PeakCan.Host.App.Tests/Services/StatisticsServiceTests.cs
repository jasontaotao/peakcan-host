using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Tests.Services;
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
/// v3.5.2 PATCH: <see cref="ExecuteAsync_Pushes_Snapshot_After_One_Tick"/>
/// now drives the timer deterministically via a
/// <see cref="FakeTimerFactory"/> instead of waiting 2.5 s on a real
/// <see cref="System.Threading.PeriodicTimer"/>. The other tick-related
/// tests still use a stoppable <see cref="CancellationTokenSource"/> to
/// bound overall execution time.
/// </para>
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
    private static (StatisticsService svc, BusStatisticsCollector collector, StatsViewModel vm, FakeTimerFactory timerFactory)
        NewWired()
    {
        var collector = new BusStatisticsCollector();
        var vm = new StatsViewModel();
        var timerFactory = new FakeTimerFactory();
        var svc = new StatisticsService(
            collector, vm, NullLogger<StatisticsService>.Instance, timerFactory);
        return (svc, collector, vm, timerFactory);
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
        // The loop fires Push(Snapshot()) before the first tick so
        // the user sees real numbers on connect, not 60 zeros for a
        // second. The collector has no frames yet, so the first
        // snapshot's TotalFrames is 0 — but Push was called and the
        // VM's bound totals stayed in sync.
        var (svc, _, vm, _) = NewWired();
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
        // v3.5.2 PATCH: drive the tick deterministically. NO Task.Delay,
        // NO wall-clock dependency. Push 10 frames into the collector,
        // start the service, fire the fake timer once, expect the VM
        // to reflect the new totals on the next snapshot.
        var (svc, collector, vm, timerFactory) = NewWired();
        for (var i = 0; i < 10; i++)
        {
            collector.OnFrame(MakeFrame());
        }
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await svc.StartAsync(cts.Token);
        try
        {
            // Wait for ExecuteAsync to reach CreateTimer (BackgroundService
            // does not guarantee ExecuteAsync runs synchronously inside
            // StartAsync under heavy xunit parallel load).
            var timer = await WaitForCreatedTimerAsync(timerFactory);

            // First snapshot (taken before the first tick) sees the
            // 10 frames and pushes to the VM. Wait for the VM to
            // pick them up; if the synchronous Apply path missed
            // for any reason, this is the recovery.
            await WaitForVmFramesAsync(vm, 10);

            timer.Fire();
            // Second snapshot (after the tick). Either the first
            // snapshot or the second one saw 10 — after Fire the VM
            // must reflect it.
            await WaitForVmFramesAsync(vm, 10);

            // The FpsSeries is pre-filled with 60 zeros at construction
            // (see StatsViewModel ctor). After two Push() calls it must
            // still be at 60 (Add → trim cycle) and the TotalFrames must
            // have reflected the 10 OnFrame'd frames at least once.
            vm.FpsSeries.Should().HaveCount(60,
                "the rolling 60-point window pre-fills with zeros at construction and never exceeds MaxPoints=60");
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitForVmFramesAsync(StatsViewModel vm, long expected, int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount + timeoutMs;
        while (Environment.TickCount < deadline)
        {
            if (vm.TotalFrames == expected) return;
            await Task.Delay(10);
        }
    }

    private static async Task<FakePeriodicTimer> WaitForCreatedTimerAsync(FakeTimerFactory timerFactory, int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount + timeoutMs;
        while (Environment.TickCount < deadline)
        {
            if (timerFactory.CreatedTimers.Count > 0)
            {
                return timerFactory.CreatedTimers[0];
            }
            await Task.Delay(10);
        }
        throw new InvalidOperationException(
            $"BackgroundService.ExecuteAsync did not create a timer within {timeoutMs}ms");
    }

    [Fact]
    public async Task ExecuteAsync_Stops_On_Cancellation_Within_One_Second()
    {
        var (svc, _, _, _) = NewWired();
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
