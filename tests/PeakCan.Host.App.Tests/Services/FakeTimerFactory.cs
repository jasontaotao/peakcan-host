using PeakCan.Host.Core.Services;

namespace PeakCan.Host.App.Tests.Services;

/// <summary>
/// v3.5.2 PATCH: test double for <see cref="ITimerFactory"/>. Created
/// timers DO NOT tick on their own — test code calls
/// <see cref="FakePeriodicTimer.Fire"/> to advance each tick
/// deterministically. Replaces the <c>Task.Delay(1100)</c> / <c>Task.Delay(2500)</c>
/// patterns that previously left the timing-dependent tests exposed
/// to CI flake.
/// <para>
/// v3.5.4 PATCH: extended with <see cref="CreateCyclicTimer"/> so the
/// same factory can drive <see cref="ICyclicTimer"/>-shaped consumers
/// (CyclicSendService, CyclicDbcSendService). Mirrors the v3.5.2 lock-
/// protected LIFO TCS pattern: <see cref="FakeCyclicTimer.Fire(int)"/>
/// invokes the registered <c>TickCallback</c> synchronously on the
/// calling test thread.
/// </para>
/// </summary>
public sealed class FakeTimerFactory : ITimerFactory
{
    /// <summary>
    /// Every <see cref="IPeriodicTimer"/> created by this factory
    /// instance, in creation order. Tests assert on <c>Single()</c>
    /// when they expect exactly one timer, or use <c>.Last()</c> after
    /// a Start/Stop/Start cycle.
    /// </summary>
    public List<FakePeriodicTimer> CreatedTimers { get; } = new();

    /// <summary>
    /// v3.5.4 PATCH: every <see cref="ICyclicTimer"/> created by this
    /// factory, in creation order. Cyclic-send race tests assert on
    /// <c>Single()</c> when they expect one timer (Start once) or
    /// <c>.Last()</c> after Start/Stop/Start.
    /// </summary>
    public List<FakeCyclicTimer> CreatedCyclicTimers { get; } = new();

    public IPeriodicTimer CreateTimer(TimeSpan period)
    {
        var t = new FakePeriodicTimer(period);
        CreatedTimers.Add(t);
        return t;
    }

    /// <inheritdoc />
    public ICyclicTimer CreateCyclicTimer(
        Action<object?> tickCallback,
        object? state,
        TimeSpan period)
    {
        var t = new FakeCyclicTimer(tickCallback, state, period);
        CreatedCyclicTimers.Add(t);
        return t;
    }
}

/// <summary>
/// v3.5.2 PATCH: single-timer test double backed by a
/// <see cref="TaskCompletionSource{TResult}"/>. Each call to
/// <see cref="Fire"/> resolves the most-recent <see cref="WaitForNextTickAsync"/>.
/// Subsequent waits get a fresh <c>TaskCompletionSource</c> so the
/// service's <c>while (await timer.WaitForNextTickAsync(ct))</c> loop
/// sees a sequence of resolved ticks exactly like the production
/// <see cref="System.Threading.PeriodicTimer"/>.
/// <para>
/// Honors <see cref="CancellationToken"/> so a service's
/// <c>StopAsync</c> (which cancels the host's stopping token) can
/// break out of an in-flight <c>WaitForNextTickAsync</c>. Without
/// this, the fake would ignore cancellation and the test cleanup
/// path would deadlock waiting for <c>Fire()</c> that never comes.
/// </para>
/// </summary>
public sealed class FakePeriodicTimer : IPeriodicTimer
{
    private readonly List<TaskCompletionSource<bool>> _waiters = new();
    private readonly TimeSpan _period;

    public FakePeriodicTimer(TimeSpan period)
    {
        _period = period;
    }

    /// <summary>Period passed to <see cref="FakeTimerFactory.CreateTimer"/>.</summary>
    public TimeSpan Period => _period;

    /// <summary>
    /// Resolve the most-recent <see cref="WaitForNextTickAsync"/> waiter (LIFO).
    /// Lock-protected swap is atomic relative to <c>WaitForNextTickAsync</c>'s
    /// <c>Add</c>, eliminating the v3.5.2 TOCTOU race where the waiter could
    /// read <c>_tcs</c> BEFORE the swap and miss the Fire.
    /// </summary>
    public void Fire()
    {
        TaskCompletionSource<bool>? toResolve;
        lock (_waiters)
        {
            toResolve = _waiters.LastOrDefault();
            if (toResolve is not null) _waiters.RemoveAt(_waiters.Count - 1);
        }
        toResolve?.TrySetResult(true);
    }

    public Task<bool> WaitForNextTickAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_waiters) { _waiters.Add(tcs); }
        // Register cancellation to clean up the waiter
        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(() =>
            {
                lock (_waiters) { _waiters.Remove(tcs); }
                tcs.TrySetCanceled(cancellationToken);
            });
        }
        return tcs.Task;
    }

    public ValueTask DisposeAsync()
    {
        lock (_waiters)
        {
            foreach (var w in _waiters) w.TrySetCanceled();
            _waiters.Clear();
        }
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// v3.5.4 PATCH: callback-driven cyclic-timer test double. Each
/// <see cref="Fire(int)"/> call invokes the registered
/// <c>TickCallback</c> synchronously on the calling test thread,
/// passing the captured <see cref="State"/>. Mirrors the v3.5.2
/// <see cref="FakePeriodicTimer"/> lock-protected pattern: the callback
/// is captured at construction time so the test owns timing entirely.
/// <para>
/// Unlike <see cref="FakePeriodicTimer"/> (which uses TCS to await the
/// next tick), the callback-driven shape has no waiter list — the
/// callback runs inline on <see cref="Fire(int)"/>. This matches the
/// production <see cref="System.Threading.Timer"/>'s callback
/// semantics (fire and forget on a ThreadPool thread) without
/// introducing a ThreadPool dependency in tests.
/// </para>
/// </summary>
public sealed class FakeCyclicTimer : ICyclicTimer
{
    private readonly Action<object?> _tickCallback;
    private readonly object? _state;
    private readonly TimeSpan _period;
    private int _disposed;

    public FakeCyclicTimer(Action<object?> tickCallback, object? state, TimeSpan period)
    {
        _tickCallback = tickCallback ?? throw new ArgumentNullException(nameof(tickCallback));
        _state = state;
        _period = period;
    }

    /// <summary>Period passed to <see cref="FakeTimerFactory.CreateCyclicTimer"/>.</summary>
    public TimeSpan Period => _period;

    /// <summary>Callback registered at construction time (production equivalent: the ctor-arg callback on <see cref="System.Threading.Timer"/>).</summary>
    public Action<object?> TickCallback => _tickCallback;

    /// <summary>State object that the next <see cref="Fire(int)"/> will pass to <see cref="TickCallback"/>.</summary>
    public object? State => _state;

    /// <summary>Number of times <see cref="Fire(int)"/> has been called since construction.</summary>
    public int FireCount { get; private set; }

    /// <summary>
    /// Invoke <see cref="TickCallback"/> with <see cref="State"/>
    /// exactly once. No-op if the timer has been disposed (matches the
    /// production <see cref="System.Threading.Timer.Dispose"/> contract:
    /// in-flight callbacks may still run after Dispose, but new fires
    /// from the fake are explicit test intent — guarding here keeps
    /// tests deterministic).
    /// </summary>
    public void Fire()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        FireCount++;
        _tickCallback(_state);
    }

    /// <summary>
    /// Invoke <see cref="TickCallback"/> <paramref name="count"/> times.
    /// Convenience for tests that need to advance N ticks in one shot
    /// (e.g. counting multi-tick success/failure accumulation).
    /// </summary>
    public void Fire(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        for (int i = 0; i < count; i++) Fire();
    }

    /// <summary>
    /// v3.5.4 PATCH: cadence reconfiguration. The fake ignores the new
    /// values because tests own timing entirely — calling
    /// <see cref="Change"/> here is a no-op so the production-side
    /// <c>Timer.Change</c> substitution is exercised by the unit tests
    /// without leaking fake-state.
    /// </summary>
    public void Change(TimeSpan dueTime, TimeSpan period)
    {
        // Intentionally empty — the fake doesn't consult cadence.
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
    }
}
