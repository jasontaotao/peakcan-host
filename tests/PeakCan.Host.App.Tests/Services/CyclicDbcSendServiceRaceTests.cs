using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;
using PeakCan.Host.Core.Services;
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
/// v3.5.4 PATCH: switched to a <see cref="FakeTimerFactory"/> and drive
/// ticks deterministically via <c>FakeCyclicTimer.Fire()</c>. The
/// pre-refactor version waited on wall-clock for the timer to fire
/// (with a 3-retry <c>CyclicTimerTestHarness</c> masking transient
/// flakes); v3.5.3 CI hit
/// <c>Encode_Failure_Increments_FailureCount_Not_SuccessCount</c> here
/// on the first attempt — the documented "out of scope" pre-existing
/// flake from <c>CyclicTimerTestHarness</c>. The factory seam removes
/// the timing-luck dependency entirely.
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
        var factory = new FakeTimerFactory();
        var svc = new CyclicDbcSendService(
            new DbcEncodeService(), send, NullLogger<CyclicDbcSendService>.Instance, factory);
        svc.Start(() => (MakeMessage(0x100), new Dictionary<string, double> { ["S"] = 1 }),
                  TimeSpan.FromMilliseconds(20));

        var timer = factory.CreatedCyclicTimers.Single();
        timer.Fire();
        timer.Fire();
        send.CallCount.Should().Be(2, "two Fire() calls yield two SendAsync invocations");

        svc.Stop();

        // Post-Stop Fire(): the timer was disposed; FakeCyclicTimer
        // guards with the _disposed flag, so no callback runs.
        timer.Fire();
        timer.Fire();

        // Drain briefly so any in-flight SendAsync callback completes
        // its observation of _cts.Token. With FakeCyclicTimer.Fire()
        // running the callback synchronously, the call above already
        // serialized — but drain briefly to match production-thread
        // semantics and to keep the test deterministic.
        await Task.Delay(20);

        send.CallCount.Should().Be(2,
            "after Stop, Fire() must not produce additional SendAsync calls");
    }

    [Fact]
    public void OnTimerTick_Generation_Mismatch_Does_Not_Send()
    {
        var send = new CountingSendService();
        var factory = new FakeTimerFactory();
        var svc = new CyclicDbcSendService(
            new DbcEncodeService(), send, NullLogger<CyclicDbcSendService>.Instance, factory);

        // First generation: id=0x100
        svc.Start(() => (MakeMessage(0x100), new Dictionary<string, double> { ["S"] = 1 }),
                  TimeSpan.FromMilliseconds(20));
        var timer1 = factory.CreatedCyclicTimers.Last();
        timer1.Fire();
        timer1.Fire();
        svc.Stop();

        int beforeSecondStart = send.CallCount;

        // Second generation: id=0x200 — bumps the generation counter so
        // queued callbacks from gen=1 with stale snapshots are dropped.
        svc.Start(() => (MakeMessage(0x200), new Dictionary<string, double> { ["S"] = 1 }),
                  TimeSpan.FromMilliseconds(20));
        var timer2 = factory.CreatedCyclicTimers.Last();
        timer2.Fire();
        timer2.Fire();
        svc.Stop();

        var idsFromSecondCycle = send.SentIds.Skip(beforeSecondStart).ToList();
        idsFromSecondCycle.Should().NotBeEmpty("second generation must send at least one frame");
        idsFromSecondCycle.Should().OnlyContain(id => id == 0x200u,
            "old-generation ticks should be dropped by the generation mismatch check");
    }

    [Fact]
    public void Encode_Failure_Increments_FailureCount_Not_SuccessCount()
    {
        var send = new CountingSendService();
        var factory = new FakeTimerFactory();
        var svc = new CyclicDbcSendService(
            new DbcEncodeService(), send, NullLogger<CyclicDbcSendService>.Instance, factory);
        var msg = MakeMessage(0x100);

        // Out-of-range value (200.0 vs [0, 100]) → DbcEncodeException
        svc.Start(() => (msg, new Dictionary<string, double> { ["S"] = 200.0 }),
                  TimeSpan.FromMilliseconds(20));
        var timer = factory.CreatedCyclicTimers.Single();
        // v3.5.4 PATCH: deterministic 3-tick advance — replaces the
        // pre-refactor `WaitUntilAsync(() => svc.FailureCount > 0, 500ms)`
        // pattern that v3.5.3 CI hit on first attempt.
        timer.Fire(3);
        svc.Stop();

        svc.FailureCount.Should().Be(3, "3 encode failures each increment FailureCount once");
        svc.SuccessCount.Should().Be(0);
        send.CallCount.Should().Be(0, "no frame should reach SendAsync when encode throws");
    }

    [Fact]
    public void Send_Success_Increments_SuccessCount_Not_FailureCount()
    {
        var send = new CountingSendService { NextResult = Result<Unit>.Ok(default) };
        var factory = new FakeTimerFactory();
        var svc = new CyclicDbcSendService(
            new DbcEncodeService(), send, NullLogger<CyclicDbcSendService>.Instance, factory);
        svc.Start(() => (MakeMessage(0x100), new Dictionary<string, double> { ["S"] = 1 }),
                  TimeSpan.FromMilliseconds(20));
        var timer = factory.CreatedCyclicTimers.Single();
        timer.Fire(3);
        svc.Stop();

        svc.SuccessCount.Should().Be(3, "3 successful sends each increment SuccessCount once");
        svc.FailureCount.Should().Be(0);
    }

    [Fact]
    public void Send_Failure_Increments_FailureCount_Not_SuccessCount()
    {
        var send = new CountingSendService
        {
            NextResult = Result<Unit>.Fail(ErrorCode.HardwareNotAvailable, "TX_ERROR")
        };
        var factory = new FakeTimerFactory();
        var svc = new CyclicDbcSendService(
            new DbcEncodeService(), send, NullLogger<CyclicDbcSendService>.Instance, factory);
        svc.Start(() => (MakeMessage(0x100), new Dictionary<string, double> { ["S"] = 1 }),
                  TimeSpan.FromMilliseconds(20));
        var timer = factory.CreatedCyclicTimers.Single();
        timer.Fire(3);
        svc.Stop();

        svc.FailureCount.Should().Be(3, "3 failed sends each increment FailureCount once");
        svc.SuccessCount.Should().Be(0);
    }

    /// <summary>
    /// v1.6.2 PATCH Item 1b + v3.5.4: verifies that
    /// <see cref="CyclicDbcSendService.Stop"/> cancels the in-flight
    /// <see cref="SendService.SendAsync"/> via the
    /// <see cref="CancellationToken"/> it passes to the underlying channel.
    /// </summary>
    [Fact]
    public async Task Stop_during_inflight_tick_cancels_SendAsync_via_ct()
    {
        var send = new CountingSendService();
        var factory = new FakeTimerFactory();
        var svc = new CyclicDbcSendService(
            new DbcEncodeService(), send, NullLogger<CyclicDbcSendService>.Instance, factory);
        svc.Start(() => (MakeMessage(0x100), new Dictionary<string, double> { ["S"] = 1 }),
                  TimeSpan.FromMilliseconds(20));

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

    [Fact]
    public void SuccessCount_And_FailureCount_Exposed_Via_Properties()
    {
        var svc = new CyclicDbcSendService(
            new DbcEncodeService(), new CountingSendService(), NullLogger<CyclicDbcSendService>.Instance,
            new FakeTimerFactory());
        svc.SuccessCount.Should().Be(0);
        svc.FailureCount.Should().Be(0);
    }
}