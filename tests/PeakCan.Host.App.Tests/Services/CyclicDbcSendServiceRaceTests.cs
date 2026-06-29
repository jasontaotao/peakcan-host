// CyclicDbcSendServiceRaceTests known transient-flaky (memory v1.2.12 lesson 4);
// harness (v1.6.1 PATCH Item 3) wraps the "wait for timer to fire" checks
// with 3 internal retries so CI no longer depends on human re-trigger.
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Tests.TestHelpers;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;
using Xunit;
using ValueType = PeakCan.Host.Core.Dbc.ValueType;

namespace PeakCan.Host.App.Tests.Services;

/// <summary>
/// v1.5.1 PATCH Item 2 (Periodic DBC send) race regression: these tests
/// pin the lock + generation + stale-timer drop invariants that
/// <see cref="CyclicDbcSendService"/> must guarantee, mirroring
/// <c>CyclicSendServiceRaceTests.cs</c>. Per spec Decision 7, the service
/// intentionally does NOT share code with <see cref="CyclicSendService"/>;
/// each service carries its own independent race-test suite.
/// <para>
/// Each test exercises a stop-vs-tick or generation-mismatch race window.
/// They are known transient-flaky on CI runners (memory v1.2.12 lesson 4);
/// re-run the suite up to 3× if a single test fails.
/// </para>
/// </summary>
public class CyclicDbcSendServiceRaceTests
{
    /// <summary>
    /// Hand-rolled <see cref="SendService"/> subclass that counts every
    /// <see cref="SendAsync"/> invocation and records the CAN IDs seen.
    /// </summary>
    private sealed class CountingSendService : SendService
    {
        public CountingSendService() : base(NullLogger<SendService>.Instance) { }
        public Result<Unit> NextResult { get; set; } = Result<Unit>.Ok(default);
        public int CallCount { get; private set; }
        public List<uint> SentIds { get; } = new();
        // v1.6.2 PATCH Item 1b: track the CT observed by the most recent SendAsync
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

    private static Signal SignalUnsigned(string name) =>
        new(name, 0, 8, ByteOrder.LittleEndian, ValueType.Unsigned, 1, 0, 0, 100, "", Array.Empty<string>());

    private static Message MakeMessage(uint id) =>
        new(id, $"Msg{id:X}", 8, "Test",
            new[] { SignalUnsigned("S") },
            IsMultiplexed: false,
            MultiplexorSignalIndex: null);

    [Fact]
    public async Task OnTimerTick_After_Stop_Does_Not_Send()
    {
        var send = new CountingSendService();
        var svc = new CyclicDbcSendService(
            new DbcEncodeService(), send, NullLogger<CyclicDbcSendService>.Instance);
        svc.Start(() => (MakeMessage(0x100), new Dictionary<string, double> { ["S"] = 1 }),
                  TimeSpan.FromMilliseconds(20));

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

        await Task.Delay(150);
        int afterStopCount = send.CallCount;

        (afterStopCount - beforeStopCount).Should().BeLessThanOrEqualTo(1,
            "after Stop, at most one in-flight tick may complete; any more is the race regression");
    }

    [Fact]
    public async Task OnTimerTick_Generation_Mismatch_Does_Not_Send()
    {
        var send = new CountingSendService();
        var svc = new CyclicDbcSendService(
            new DbcEncodeService(), send, NullLogger<CyclicDbcSendService>.Instance);

        // First generation: id=0x100
        svc.Start(() => (MakeMessage(0x100), new Dictionary<string, double> { ["S"] = 1 }),
                  TimeSpan.FromMilliseconds(20));
        await Task.Delay(60);
        svc.Stop();
        await Task.Delay(40);
        int beforeSecondStart = send.CallCount;

        // Second generation: id=0x200 — bumps the generation counter so
        // queued callbacks from gen=1 with stale snapshots are dropped.
        svc.Start(() => (MakeMessage(0x200), new Dictionary<string, double> { ["S"] = 1 }),
                  TimeSpan.FromMilliseconds(20));
        await Task.Delay(80);
        svc.Stop();

        var idsFromSecondCycle = send.SentIds.Skip(beforeSecondStart).ToList();
        idsFromSecondCycle.Should().NotBeEmpty("second generation must send at least one frame");
        idsFromSecondCycle.Should().OnlyContain(id => id == 0x200u,
            "old-generation ticks should be dropped by the generation mismatch check");
    }

    [Fact]
    public async Task Encode_Failure_Increments_FailureCount_Not_SuccessCount()
    {
        var send = new CountingSendService();
        var svc = new CyclicDbcSendService(
            new DbcEncodeService(), send, NullLogger<CyclicDbcSendService>.Instance);
        var msg = MakeMessage(0x100);

        // Out-of-range value (200.0 vs [0, 100]) → DbcEncodeException
        svc.Start(() => (msg, new Dictionary<string, double> { ["S"] = 200.0 }),
                  TimeSpan.FromMilliseconds(20));
        await Task.Delay(120);
        svc.Stop();

        svc.FailureCount.Should().BeGreaterThan(0);
        svc.SuccessCount.Should().Be(0);
        send.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Send_Success_Increments_SuccessCount_Not_FailureCount()
    {
        var send = new CountingSendService { NextResult = Result<Unit>.Ok(default) };
        var svc = new CyclicDbcSendService(
            new DbcEncodeService(), send, NullLogger<CyclicDbcSendService>.Instance);
        svc.Start(() => (MakeMessage(0x100), new Dictionary<string, double> { ["S"] = 1 }),
                  TimeSpan.FromMilliseconds(20));
        await Task.Delay(120);
        svc.Stop();

        svc.SuccessCount.Should().BeGreaterThan(0);
        svc.FailureCount.Should().Be(0);
    }

    [Fact]
    public async Task Send_Failure_Increments_FailureCount_Not_SuccessCount()
    {
        var send = new CountingSendService
        {
            NextResult = Result<Unit>.Fail(ErrorCode.HardwareNotAvailable, "TX_ERROR")
        };
        var svc = new CyclicDbcSendService(
            new DbcEncodeService(), send, NullLogger<CyclicDbcSendService>.Instance);
        svc.Start(() => (MakeMessage(0x100), new Dictionary<string, double> { ["S"] = 1 }),
                  TimeSpan.FromMilliseconds(20));
        await Task.Delay(120);
        svc.Stop();

        svc.FailureCount.Should().BeGreaterThan(0);
        svc.SuccessCount.Should().Be(0);
    }

    /// <summary>
    /// v1.6.2 PATCH Item 1b: verifies that <see cref="CyclicDbcSendService.Stop"/>
    /// cancels the in-flight <see cref="SendService.SendAsync"/> via the
    /// <see cref="CancellationToken"/> it passes to the underlying channel.
    /// <para>
    /// After <see cref="CyclicDbcSendService.Start"/>, the timer fires every
    /// 20ms and the encode + send path runs each tick; we wait for at least
    /// one tick (so <c>SendAsync</c> has been called once with a non-cancelled
    /// token), then call <see cref="CyclicDbcSendService.Stop"/>. A brief drain
    /// (50ms) lets any in-flight tick reach its <c>await SendAsync</c> with the
    /// cancelled token. The last observed CT must be cancelled.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Stop_during_inflight_tick_cancels_SendAsync_via_ct()
    {
        var send = new CountingSendService();
        var svc = new CyclicDbcSendService(
            new DbcEncodeService(), send, NullLogger<CyclicDbcSendService>.Instance);
        svc.Start(() => (MakeMessage(0x100), new Dictionary<string, double> { ["S"] = 1 }),
                  TimeSpan.FromMilliseconds(20));

        await CyclicTimerTestHarness.WaitUntilAsync(
            () => send.CallCount > 0,
            TimeSpan.FromMilliseconds(500));

        svc.Stop();

        // Drain briefly so any in-flight SendAsync callback completes its
        // observation of _cts.Token. 50ms is more than enough — our
        // CountingSendService returns synchronously.
        await Task.Delay(50);

        send.LastObservedCt.IsCancellationRequested.Should().BeTrue(
            "Stop() must cancel the CTS so in-flight SendAsync receives a cancelled token");
    }

    [Fact]
    public void SuccessCount_And_FailureCount_Exposed_Via_Properties()
    {
        var svc = new CyclicDbcSendService(
            new DbcEncodeService(), new CountingSendService(), NullLogger<CyclicDbcSendService>.Instance);
        svc.SuccessCount.Should().Be(0);
        svc.FailureCount.Should().Be(0);
    }
}
