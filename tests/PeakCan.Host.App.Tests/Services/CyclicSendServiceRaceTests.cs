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

    /// <summary>
    /// v3.5.5 PATCH: regression test for in-flight <c>SendAsync</c> behavior
    /// when the channel is disposed concurrently with <c>Stop()</c>.
    /// Previously untested — <c>CyclicSendService.StopInner</c>
    /// (<c>CyclicSendService.cs:148</c>) calls <c>_timer?.Dispose()</c>
    /// which does NOT wait for the in-flight <c>OnTimerTick</c>
    /// callback. If the caller concurrently disposes the channel while
    /// <c>await _sendService.SendAsync(frame, ct)</c> is mid-await,
    /// <c>PeakCanChannel.WriteAsync</c> behavior on concurrent disposal
    /// is untested.
    /// <para>
    /// The assertion is: no unhandled exception escapes. Either
    /// <see cref="OperationCanceledException"/> or
    /// <see cref="ObjectDisposedException"/> are acceptable; a native
    /// handle crash or uncaught exception type is not.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Stop_While_InFlight_SendAsync_Disposing_Channel_Does_Not_Crash()
    {
        // Slow-fake ICanChannel that holds WriteAsync open for 200 ms
        // before completing (or throws ObjectDisposedException if
        // DisposeAsync was called first). Mirrors the existing
        // FakeChannel pattern from CyclicSendServiceTests but with a
        // configurable delay + dispose-aware throw.
        var slowChannel = new SlowFakeChannel(delayMs: 200);
        var sendService = new SendService(NullLogger<SendService>.Instance)
        {
            ActiveChannel = slowChannel
        };
        var timerFactory = new FakeTimerFactory();
        var svc = new CyclicSendService(sendService, NullLogger<CyclicSendService>.Instance, timerFactory);

        svc.Start(BuildFrame(0x100), TimeSpan.FromMilliseconds(50));
        var timer = timerFactory.CreatedCyclicTimers.Single();

        // Trigger in-flight send: Fire() runs the callback synchronously
        // but the callback awaits WriteAsync, which is held open by the
        // SlowFakeChannel delay. The continuation is queued on the
        // SynchronizationContext (or thread-pool); we'll race Stop +
        // DisposeAsync against it.
        // v3.5.6 PATCH: wait on SlowFakeChannel.WriteAsyncInvoked (a TCS
        // signaled before SlowFakeChannel's Task.Delay) instead of
        // wall-clock-guessed `await Task.Delay(50)`. Deterministic
        // guarantee that the callback has reached mid-await — robust
        // under any CI scheduler load.
        timer.Fire();
        await slowChannel.WriteAsyncInvoked;

        // Concurrently stop service and dispose channel. Both should
        // complete without throwing non-OCE / non-ObjectDisposedException.
        // Either of those is acceptable (CyclicSendService swallows
        // OperationCanceledException; SlowFakeChannel's WriteAsync may
        // throw ObjectDisposedException if DisposeAsync was called
        // first, which is the race being exercised).
        var stopTask = Task.Run(() => svc.Stop());
        var disposeTask = Task.Run(() => slowChannel.DisposeAsync().AsTask());

        await Task.WhenAll(stopTask, disposeTask).WaitAsync(TimeSpan.FromSeconds(2));

        // Fire one more time — if in-flight logic raced wrong,
        // FakeCyclicTimer re-entry after Stop would crash.
        timer.Fire(); // should be no-op since Stop was called

        // Cleanup
        svc.Dispose();
    }

    /// <summary>
    /// v3.5.5 PATCH: minimal <see cref="ICanChannel"/> test double that
    /// holds <see cref="WriteAsync"/> open for a configurable delay, then
    /// completes. If <see cref="DisposeAsync"/> is invoked first,
    /// pending <see cref="WriteAsync"/> calls throw
    /// <see cref="ObjectDisposedException"/> — exercising the
    /// concurrent-dispose-while-write race that v3.5.5 PATCH Fix 2
    /// regression-tests.
    /// <para>
    /// Mirrors the existing <c>FakeChannel</c> pattern from
    /// <c>CyclicSendServiceTests</c> but adds (a) an artificial
    /// Task.Delay in <see cref="WriteAsync"/> to keep the call
    /// mid-await while <see cref="Stop()"/> + <see cref="DisposeAsync"/>
    /// race against it, and (b) a dispose-throws branch so the race
    /// actually has something to surface.
    /// </para>
    /// </summary>
    private sealed class SlowFakeChannel : ICanChannel
    {
        private readonly TimeSpan _delay;
        // v3.5.6 PATCH: TCS signaled when WriteAsync is invoked, so tests
        // can deterministically wait for the caller's continuation to
        // reach mid-await. Replaces the wall-clock-guessed Task.Delay(50)
        // the v3.5.5 review flagged as LOW nit (worked under bounded
        // CI load, but not strictly deterministic — could flake under
        // heavier parallel load). RunContinuationsAsynchronously avoids
        // stack dives if the awaiter resumes inline.
        private readonly TaskCompletionSource _writeStartedTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;

        public ChannelId Id { get; } = new(0x51);
        public bool IsConnected { get; private set; } = true;
#pragma warning disable CS0067
        public event Action<CanFrame>? FrameReceived;
        // v3.16.9.4 PATCH: ICanChannel gained ReadLoopError event — unused
        // in this test fake, but must exist to satisfy the interface.
#pragma warning disable CS0067
        public event Action<ReadLoopError>? ReadLoopError;
#pragma warning restore CS0067
#pragma warning restore CS0067

        /// <summary>
        /// v3.5.6 PATCH: completes the moment <see cref="WriteAsync"/>
        /// is invoked (before the <c>Task.Delay</c>), so tests know the
        /// caller's continuation has reached mid-await without relying
        /// on wall-clock timing.
        /// </summary>
        public Task WriteAsyncInvoked => _writeStartedTcs.Task;

        public SlowFakeChannel(int delayMs = 200)
        {
            _delay = TimeSpan.FromMilliseconds(delayMs);
        }

        public Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default)
        { IsConnected = true; return Task.FromResult(Result<Unit>.Ok(default)); }

        public Task DisconnectAsync(CancellationToken ct = default)
        { IsConnected = false; return Task.CompletedTask; }

        public async ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
        {
            // Signal "WriteAsync has been invoked" BEFORE any await, so
            // the caller's continuation is known to be mid-await when the
            // test sees this complete.
            _writeStartedTcs.TrySetResult();

            // Honor cancellation: caller (CyclicSendService.Stop's CTS)
            // cancelling during the delay throws OCE — the
            // CyclicSendService catches it, no failure increment.
            await Task.Delay(_delay, ct).ConfigureAwait(false);

            // Race: if DisposeAsync fired first, simulate a
            // peakcan-channel WriteAsync on a disposed handle by
            // throwing ObjectDisposedException. Otherwise complete
            // normally. Use ThrowIf (CA1513) instead of `throw new`.
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return Result<Unit>.Ok(default);
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _disposed, 1);
            IsConnected = false;
            return ValueTask.CompletedTask;
        }
    }
}