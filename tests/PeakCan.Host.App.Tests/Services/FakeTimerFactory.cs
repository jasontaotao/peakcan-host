using PeakCan.Host.Core.Services;

namespace PeakCan.Host.App.Tests.Services;

/// <summary>
/// v3.5.2 PATCH: test double for <see cref="ITimerFactory"/>. Created
/// timers DO NOT tick on their own — test code calls
/// <see cref="FakePeriodicTimer.Fire"/> to advance each tick
/// deterministically. Replaces the <c>Task.Delay(1100)</c> / <c>Task.Delay(2500)</c>
/// patterns that previously left the timing-dependent tests exposed
/// to CI flake.
/// </summary>
public sealed class FakeTimerFactory : ITimerFactory
{
    /// <summary>
    /// Every timer created by this factory instance, in creation order.
    /// Tests assert on <c>Single()</c> when they expect exactly one
    /// timer, or use <c>.Last()</c> after a Start/Stop/Start cycle.
    /// </summary>
    public List<FakePeriodicTimer> CreatedTimers { get; } = new();

    public IPeriodicTimer CreateTimer(TimeSpan period)
    {
        var t = new FakePeriodicTimer(period);
        CreatedTimers.Add(t);
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
