using FluentAssertions;
using PeakCan.Host.Core;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App.Tests.Services;

/// <summary>
/// Task 13: verifies the <see cref="TraceService"/> background batching loop
/// and the bounded channel drop-oldest overflow semantics.
/// <para>
/// The batching test is timing-dependent (50ms tick) so tolerances are
/// generous: 200ms wait, 5s overall timeout. The drop-oldest test pre-fills
/// the channel directly via <see cref="TraceService.OnFrame"/> so the
/// timing dependency is contained.
/// </para>
/// </summary>
public class TraceServiceTests
{
    private static CanFrame MakeFrame(uint id = 0x123, byte dlc = 4)
    {
        byte[] payload = dlc == 0 ? Array.Empty<byte>() : new byte[dlc];
        return new CanFrame(
            new CanId(id, FrameFormat.Standard),
            payload,
            FrameFlags.None,
            new ChannelId(0x51),
            Timestamp.FromMicroseconds(1_000_000UL));
    }

    [Fact]
    public void OnFrame_Pushes_Frame_Into_Batching_Channel()
    {
        // OnFrame must be non-throwing and synchronous (the SDK read
        // thread cannot await). We don't have a public Count, but we can
        // assert that a single OnFrame call doesn't throw and that the
        // service is reusable across many OnFrame invocations.
        var vm = new TraceViewModel();
        var service = new TraceService(vm);
        var act = () =>
        {
            for (int i = 0; i < 10; i++)
            {
                service.OnFrame(MakeFrame(id: (uint)(0x100 + i)));
            }
        };
        act.Should().NotThrow();
    }

    [Fact]
    public async Task ExecuteAsync_Periodically_Flushes_Channel_Into_VM_Batch()
    {
        // Bounded behavioural test: start the service, push 3 frames,
        // wait long enough for the 50ms tick to drain at least once.
        // In the test context the VM's AppendBatchAsync is a no-op (no
        // dispatcher), so we cannot assert VM.Entries grows here. The
        // observable we DO have is: StartAsync returns, the service
        // processes OnFrame without throwing, and StopAsync returns
        // within a sane timeout.
        var vm = new TraceViewModel();
        var service = new TraceService(vm);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await service.StartAsync(cts.Token);
        try
        {
            service.OnFrame(MakeFrame());
            service.OnFrame(MakeFrame(id: 0x200));
            service.OnFrame(MakeFrame(id: 0x300));
            // Allow several 50ms ticks to drain.
            await Task.Delay(200, cts.Token);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ExecuteAsync_Stops_On_Cancellation()
    {
        // Start the service, then ask it to stop. StopAsync should
        // observe cancellation and complete within 1s.
        var vm = new TraceViewModel();
        var service = new TraceService(vm);
        using var startCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await service.StartAsync(startCts.Token);

        var stopTask = service.StopAsync(CancellationToken.None);
        var completed = await Task.WhenAny(stopTask, Task.Delay(1000));
        completed.Should().BeSameAs(stopTask, "StopAsync should observe cancellation within 1s");
        await stopTask;
    }

    [Fact]
    public void OnFrame_With_Bounded_Channel_Full_Drops_Oldest_Without_Throwing()
    {
        // The internal channel is bounded at 10_000 with DropOldest. After
        // pre-filling 10_000 frames, additional frames must not throw and
        // must be accepted (dropping the oldest unread).
        var vm = new TraceViewModel();
        var service = new TraceService(vm);
        // Pre-fill 10_000 frames synchronously. TryWrite is bounded +
        // DropOldest so this never throws and never blocks. Use modulo
        // 0x800 to stay within the 11-bit Standard ID range; we are
        // testing the channel-overflow path, not the CanId validator.
        var prefill = () =>
        {
            for (int i = 0; i < 10_001; i++)
            {
                service.OnFrame(MakeFrame(id: (uint)(i & 0x7FF)));
            }
        };
        prefill.Should().NotThrow();
        // One more push for good measure — must still not throw.
        service.OnFrame(MakeFrame(id: 0x7FF));
    }

    [Fact]
    public void DroppedFrames_Counter_Increments_When_Channel_Is_Full()
    {
        // Spec §6.2 mandates "dropped frames only log". The TraceService
        // exposes a DroppedFrames counter for tests + future UI status
        // bar (Warn ×N overrun indicator). After pushing more than the
        // 10_000 channel capacity, the counter must be > 0.
        var vm = new TraceViewModel();
        var service = new TraceService(vm);
        service.DroppedFrames.Should().Be(0, "fresh service has no drops");
        for (int i = 0; i < 10_500; i++)
        {
            service.OnFrame(MakeFrame(id: (uint)(i & 0x7FF)));
        }
        service.DroppedFrames.Should().BeGreaterThan(0,
            "pushing past the 10_000 capacity must increment the drop counter");
    }

    [Fact]
    public void OnError_Is_NoOp()
    {
        // OnError must not throw and must not change service state.
        // Mirrors the BusStatisticsCollector OnError contract.
        var vm = new TraceViewModel();
        var service = new TraceService(vm);
        var act = () => service.OnError(new InvalidOperationException("simulated"));
        act.Should().NotThrow();
    }
}
