using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Tests.TestHelpers;
using PeakCan.Host.Core;
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

        public override ValueTask<Result<Unit>> SendAsync(CanFrame frame, CancellationToken ct = default)
        {
            CallCount++;
            SentIds.Add(frame.Id.Raw);
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
        var svc = new CyclicSendService(send, NullLogger<CyclicSendService>.Instance);
        svc.Start(BuildFrame(0x100), TimeSpan.FromMilliseconds(20));

        // Let the timer fire a few times so we have a non-zero baseline.
        // v1.6.1 PATCH Item 3: harness wraps the wait with 3 internal
        // retries (5ms polling per attempt) so transient CI flakes no
        // longer depend on human re-trigger.
        await CyclicTimerTestHarness.AssertWithinAsync(
            () => send.CallCount > 0,
            TimeSpan.FromMilliseconds(500),
            "timer ticks at least once before Stop");

        svc.Stop();
        int beforeStopCount = send.CallCount;

        // After Stop, allow plenty of wall-clock time for any already-queued
        // tick to fire. With lock-snapshot at top of OnTimerTick, queued
        // ticks should observe _isRunning=false and bail; and the
        // generation bump in StopInner invalidates any tick whose body
        // already started before Stop flipped the flag. The only
        // acceptable leak is the single tick whose `OnTimerTick` body had
        // already passed the lock check and is mid-await on SendAsync
        // when Stop runs — its `await` will complete (no cancellation
        // token is propagated to SendAsync from OnTimerTick), but that's
        // at most 1 in-flight frame. Two or more leaks is the race
        // regression.
        await Task.Delay(150);
        int afterStopCount = send.CallCount;

        (afterStopCount - beforeStopCount).Should().BeLessThanOrEqualTo(1,
            "after Stop, at most one in-flight tick may complete its SendAsync; any more is the race regression");
    }

    [Fact]
    public async Task OnTimerTick_Generation_Mismatch_Does_Not_Send()
    {
        var send = new CountingSendService();
        var svc = new CyclicSendService(send, NullLogger<CyclicSendService>.Instance);

        // First generation: id=0x100
        svc.Start(BuildFrame(0x100), TimeSpan.FromMilliseconds(20));
        await Task.Delay(60);
        svc.Stop();
        // Let any old-generation in-flight callbacks finish so they don't
        // get misattributed to the second-generation cycle.
        await Task.Delay(40);
        int beforeSecondStart = send.CallCount;

        // Second generation: id=0x200 — bumps the generation counter so
        // queued callbacks from gen=1 with stale snapshots are dropped.
        svc.Start(BuildFrame(0x200), TimeSpan.FromMilliseconds(20));
        await Task.Delay(80);
        svc.Stop();

        // After the second Start, only 0x200 frames should reach SendAsync.
        // The 0x100 frames captured by SentIds must all predate beforeSecondStart.
        var idsFromSecondCycle = send.SentIds.Skip(beforeSecondStart).ToList();
        idsFromSecondCycle.Should().NotBeEmpty("second generation must send at least one frame");
        idsFromSecondCycle.Should().OnlyContain(id => id == 0x200u,
            "old-generation ticks should be dropped by the generation mismatch check");
    }

    [Fact]
    public async Task Send_Failure_Increments_FailureCount_Not_SuccessCount()
    {
        var send = new CountingSendService
        {
            NextResult = Result<Unit>.Fail(ErrorCode.HardwareNotAvailable, "TX_ERROR")
        };
        var svc = new CyclicSendService(send, NullLogger<CyclicSendService>.Instance);
        svc.Start(BuildFrame(0x100), TimeSpan.FromMilliseconds(20));
        await Task.Delay(80);
        svc.Stop();

        svc.FailureCount.Should().BeGreaterThan(0);
        svc.SuccessCount.Should().Be(0);
    }

    [Fact]
    public async Task Send_Success_Increments_SuccessCount_Not_FailureCount()
    {
        var send = new CountingSendService { NextResult = Result<Unit>.Ok(default) };
        var svc = new CyclicSendService(send, NullLogger<CyclicSendService>.Instance);
        svc.Start(BuildFrame(0x100), TimeSpan.FromMilliseconds(20));
        await Task.Delay(80);
        svc.Stop();

        svc.SuccessCount.Should().BeGreaterThan(0);
        svc.FailureCount.Should().Be(0);
    }

    [Fact]
    public void SuccessCount_And_FailureCount_Exposed_Via_Properties()
    {
        var svc = new CyclicSendService(new CountingSendService(), NullLogger<CyclicSendService>.Instance);
        svc.SuccessCount.Should().Be(0);
        svc.FailureCount.Should().Be(0);
    }
}
