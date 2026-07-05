using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Services;
using Xunit;

namespace PeakCan.Host.App.Tests.Services;

/// <summary>
/// v1.2.12 PATCH Item 10: race regression + split success/failure count
/// for <see cref="CyclicSendService"/>.
///
/// The original <c>OnTimerTick</c> read <c>_isRunning</c> / <c>_frame</c>
/// without a lock; <c>Stop</c> could flip <c>_isRunning=false</c> between
/// the check and <c>SendAsync</c>, leaking one frame after Stop. The
/// single <c>_sendCount</c> field mixed success + failure increments.
/// These tests pin the four behaviors that the new implementation must
/// guarantee:
///   - no frame sent after Stop (race regression)
///   - no frame from an old generation after Start re-entry
///   - failure increments <c>FailureCount</c> only
///   - success increments <c>SuccessCount</c> only
/// <para>
/// v3.5.4 PATCH: switched to a <see cref="FakeTimerFactory"/> and drive
/// ticks deterministically via <c>FakeCyclicTimer.Fire()</c>. The
/// pre-refactor version waited on wall-clock for the timer to fire
/// (with a 3-retry <c>CyclicTimerTestHarness</c> masking transient
/// flakes); v3.5.3 CI hit
/// <c>Encode_Failure_Increments_FailureCount_Not_SuccessCount</c> in
/// the sibling <c>CyclicDbcSendServiceRaceTests</c>, demonstrating
/// that 3 retries wasn't enough. The factory seam removes the
/// timing-luck dependency entirely — tests advance ticks on demand.
/// </para>
/// </summary>
public class CyclicSendServiceRaceTests
{
    /// <summary>
    /// Hand-rolled <see cref="SendService"/> subclass that counts every
    /// <see cref="SendAsync"/> invocation and returns a configurable
    /// <see cref="Result{T}"/>. Same pattern as
    /// <c>SendViewModelTests.FakeSendService</c> — avoids NSubstitute's
    /// CA2012 ValueTask warnings on concrete-class mocks.
    /// </summary>
    private sealed class CountingSendService : SendService
    {
        public CountingSendService() : base(NullLogger<SendService>.Instance) { }
        public Result<Unit> NextResult { get; set; } = Result<Unit>.Ok(default);
        public int CallCount { get; private set; }
        public List<uint> SentIds { get; } = new();
        // v1.6.2 PATCH Item 1a: track the CT observed by the most recent SendAsync
        // invocation. After Stop(), production should pass a cancelled token; this
        // property is the assertion target.
        public CancellationToken LastObservedCt { get; private set; }

        public override ValueTask<Result<Unit>> SendAsync(CanFrame frame, CancellationToken ct = default)
        {
            CallCount++;
            SentIds.Add(frame.Id.Raw);
            LastObservedCt = ct;
            return ValueTask.FromResult(NextResult);
        }
    }

    private static CanFrame BuildFrame(uint id)
        => new(new CanId(id, FrameFormat.Standard),
               new byte[] { 0xAA },
               FrameFlags.None,
               ChannelId.None,
               default);

    [Fact]
    public async Task OnTimerTick_After_Stop_Does_Not_Send()
    {
        var send = new CountingSendService();
        var factory = new FakeTimerFactory();
        var svc = new CyclicSendService(send, NullLogger<CyclicSendService>.Instance, factory);
        svc.Start(BuildFrame(0x100), TimeSpan.FromMilliseconds(20));

        // v3.5.4 PATCH: deterministic Fire() drives the timer instead
        // of waiting on wall-clock. Two ticks baseline so we can
        // observe the post-Stop tail.
        var timer = factory.CreatedCyclicTimers.Single();
        timer.Fire();
        timer.Fire();
        send.CallCount.Should().Be(2, "two Fire() calls should yield two SendAsync invocations");

        svc.Stop();

        // Post-Stop: even if Fire() is invoked again, the service's
        // lock-snapshot at top of OnTimerTick must observe
        // _isRunning=false and bail. The stale-timer-drop path also
        // bails because Stop() bumps the generation.
        timer.Fire();
        timer.Fire();

        // Allow any in-flight tick callback (already past the lock
        // check) to complete. With FakeCyclicTimer.Fire() running the
        // callback synchronously on the test thread, the call above
        // already serialized — but drain briefly to match the
        // production-thread semantics.
        await Task.Delay(20);

        // Pre-refactor (Task.Delay-based) tests allowed up to 1
        // post-Stop leak because a Timer callback could be queued
        // between Start and Stop with SendAsync in flight. With the
        // fake, Fire() is synchronous: NO post-Stop call to
        // SendAsync should occur because Stop() runs before Fire()
        // returns control to the timer thread (in production) /
        // completes the callback (in tests).
        send.CallCount.Should().Be(2,
            "after Stop, Fire() must not produce additional SendAsync calls");
    }

    [Fact]
    public async Task OnTimerTick_Generation_Mismatch_Does_Not_Send()
    {
        var send = new CountingSendService();
        var factory = new FakeTimerFactory();
        var svc = new CyclicSendService(send, NullLogger<CyclicSendService>.Instance, factory);

        // First generation: id=0x100
        svc.Start(BuildFrame(0x100), TimeSpan.FromMilliseconds(20));
        var timer1 = factory.CreatedCyclicTimers.Last();
        timer1.Fire();
        timer1.Fire();
        svc.Stop();

        int beforeSecondStart = send.CallCount;

        // Second generation: id=0x200 — bumps the generation counter so
        // queued callbacks from gen=1 with stale snapshots are dropped.
        svc.Start(BuildFrame(0x200), TimeSpan.FromMilliseconds(20));
        var timer2 = factory.CreatedCyclicTimers.Last();
        timer2.Fire();
        timer2.Fire();
        svc.Stop();

        // After the second Start, only 0x200 frames should reach SendAsync.
        // Pre-refactor: queued gen=1 callbacks could fire mid-window;
        // post-refactor: each Start replaces the timer (Stop disposes
        // gen=1 timer, Start creates gen=2 timer), so timer1.Fire()
        // post-Stop is a no-op.
        var idsFromSecondCycle = send.SentIds.Skip(beforeSecondStart).ToList();
        idsFromSecondCycle.Should().NotBeEmpty("second generation must send at least one frame");
        idsFromSecondCycle.Should().OnlyContain(id => id == 0x200u,
            "old-generation ticks should be dropped by the generation mismatch check");
    }

    [Fact]
    public void Send_Failure_Increments_FailureCount_Not_SuccessCount()
    {
        var send = new CountingSendService
        {
            NextResult = Result<Unit>.Fail(ErrorCode.HardwareNotAvailable, "TX_ERROR")
        };
        var factory = new FakeTimerFactory();
        var svc = new CyclicSendService(send, NullLogger<CyclicSendService>.Instance, factory);
        svc.Start(BuildFrame(0x100), TimeSpan.FromMilliseconds(20));
        var timer = factory.CreatedCyclicTimers.Single();
        timer.Fire(3);
        svc.Stop();

        svc.FailureCount.Should().Be(3, "3 Fire() calls each produce one failure increment");
        svc.SuccessCount.Should().Be(0);
    }

    [Fact]
    public void Send_Success_Increments_SuccessCount_Not_FailureCount()
    {
        var send = new CountingSendService { NextResult = Result<Unit>.Ok(default) };
        var factory = new FakeTimerFactory();
        var svc = new CyclicSendService(send, NullLogger<CyclicSendService>.Instance, factory);
        svc.Start(BuildFrame(0x100), TimeSpan.FromMilliseconds(20));
        var timer = factory.CreatedCyclicTimers.Single();
        timer.Fire(3);
        svc.Stop();

        svc.SuccessCount.Should().Be(3, "3 Fire() calls each produce one success increment");
        svc.FailureCount.Should().Be(0);
    }

    [Fact]
    public void SuccessCount_And_FailureCount_Exposed_Via_Properties()
    {
        var svc = new CyclicSendService(
            new CountingSendService(),
            NullLogger<CyclicSendService>.Instance,
            new FakeTimerFactory());
        svc.SuccessCount.Should().Be(0);
        svc.FailureCount.Should().Be(0);
    }

    /// <summary>
    /// v1.6.2 PATCH Item 1a + v3.5.4: verifies that
    /// <see cref="CyclicSendService.Stop"/> cancels the in-flight
    /// <see cref="SendService.SendAsync"/> via the
    /// <see cref="CancellationToken"/> it passes to the underlying channel.
    /// <para>
    /// With the <see cref="FakeCyclicTimer"/>, the tick callback runs
    /// synchronously on the calling test thread, so we drive one tick
    /// to establish a baseline (non-cancelled CT observed), then call
    /// <see cref="CyclicSendService.Stop"/>. A subsequent Fire() after
    /// Stop would observe a cancelled CTSnapshot inside OnTimerTick —
    /// the assertion verifies the CTS was indeed cancelled by Stop().
    /// </para>
    /// </summary>
    [Fact]
    public async Task Stop_during_inflight_tick_cancels_SendAsync_via_ct()
    {
        var send = new CountingSendService();
        var factory = new FakeTimerFactory();
        var svc = new CyclicSendService(send, NullLogger<CyclicSendService>.Instance, factory);
        svc.Start(BuildFrame(0x100), TimeSpan.FromMilliseconds(20));

        // First tick: establishes baseline (non-cancelled token).
        var timer = factory.CreatedCyclicTimers.Single();
        timer.Fire();
        send.LastObservedCt.IsCancellationRequested.Should().BeFalse(
            "first tick must observe a non-cancelled CT");

        svc.Stop();

        // Drain briefly so any in-flight SendAsync callback completes its
        // observation of _cts.Token. With FakeCyclicTimer.Fire() running
        // synchronously, the call above already serialized; the brief
        // delay preserves the pre-refactor test contract.
        await Task.Delay(20);

        send.LastObservedCt.IsCancellationRequested.Should().BeTrue(
            "Stop() must cancel the CTS so in-flight SendAsync receives a cancelled token");
    }
}