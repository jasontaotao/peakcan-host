using System.Threading;

namespace PeakCan.Host.Core.Services;

/// <summary>
/// v3.5.4 PATCH: production <see cref="ITimerFactory.CreateCyclicTimer"/>
/// implementation backed by <see cref="System.Threading.Timer"/>.
/// Mirrors the shape that <c>CyclicSendService</c> and
/// <c>CyclicDbcSendService</c> have used since v1.2.12 — a callback
/// fires on a ThreadPool thread every <c>period</c>. This class is the
/// production half of the v3.5.4 deterministic-test refactor; the
/// test half is <c>FakeCyclicTimer</c> in <c>FakeTimerFactory.cs</c>.
/// <para>
/// The factory implementation also re-exposes
/// <see cref="CreateTimer(TimeSpan)"/> for the
/// <see cref="IPeriodicTimer"/>-shaped consumers (RecordService,
/// StatisticsService, TraceService) — same shape as the v3.5.2
/// <see cref="PeriodicTimerFactory"/> but with the
/// <see cref="CreateCyclicTimer"/> extension. Register this class as
/// the singleton <see cref="ITimerFactory"/> in DI; consumers that
/// need a cyclic timer call
/// <c>_timerFactory.CreateCyclicTimer(OnTick, state, interval)</c>
/// from their Start path.
/// </para>
/// </summary>
public sealed class CyclicTimerFactory : ITimerFactory
{
    /// <inheritdoc />
    public IPeriodicTimer CreateTimer(TimeSpan period)
        => new PeriodicTimerWrapper(new System.Threading.PeriodicTimer(period));

    /// <inheritdoc />
    public ICyclicTimer CreateCyclicTimer(
        Action<object?> tickCallback,
        object? state,
        TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(tickCallback);
        var wrapper = new CyclicTimerWrapper(tickCallback, state);
        // First tick after `period`, subsequent ticks every `period` —
        // matches the pre-refactor `new Timer(cb, state, dueTime, period)`
        // signature where dueTime == period (i.e. the timer doesn't fire
        // immediately; the first tick lands after one period).
        wrapper.Arm(period, period);
        return wrapper;
    }

    // Internal so FakeCyclicTimer (in App.Tests) can share this base
    // contract if it chooses to derive from it (the production path is
    // the canonical implementation; FakeCyclicTimer currently mirrors
    // the surface independently to keep the lock-protected TCS LIFO
    // pattern encapsulated).
    internal sealed class CyclicTimerWrapper : ICyclicTimer
    {
        private readonly Action<object?> _tickCallback;
        private readonly object? _state;
        private Timer? _inner;

        internal CyclicTimerWrapper(Action<object?> tickCallback, object? state)
        {
            _tickCallback = tickCallback;
            _state = state;
        }

        public Action<object?> TickCallback => _tickCallback;

        public object? State => _state;

        /// <summary>
        /// Initialize or re-arm the underlying <see cref="Timer"/>.
        /// Production calls this once from the factory's
        /// <see cref="CreateCyclicTimer"/> and again from
        /// <see cref="Change"/> on Start re-entry. Matches
        /// <see cref="System.Threading.Timer(TimerCallback, object?, TimeSpan, TimeSpan)"/>
        /// semantics: dueTime == 0 means "fire immediately on the next
        /// ThreadPool slot"; dueTime &gt; 0 means "first tick after the
        /// due time, then every period". Pre-refactor code used
        /// <c>new Timer(cb, state, interval, interval)</c> (dueTime ==
        /// period) — preserved here for the first arm. Subsequent
        /// <see cref="Change"/> calls mirror the v1.6.2 PATCH Item 10
        /// restart path: dueTime = period.
        /// </summary>
        internal void Arm(TimeSpan dueTime, TimeSpan period)
        {
            // Replace the previous timer (if any) — matches Timer.Change
            // semantics: a disposed timer cannot be reused, so we drop
            // the old one and create a fresh Timer. Safe because the
            // service's lock+generation snapshot guarantees no stale
            // callback can land on a new generation.
            _inner?.Dispose();
            _inner = new Timer(static s =>
            {
                var self = (CyclicTimerWrapper)s!;
                self._tickCallback(self._state);
            }, this, dueTime, period);
        }

        /// <inheritdoc />
        public void Change(TimeSpan dueTime, TimeSpan period)
        {
            // If Dispose ran before Change (test path), this is a no-op.
            if (_inner is null) return;
            // Timer.Change semantics: replaces the due time and period on
            // the same underlying Timer (cheaper than rebuild). Use
            // Arm(replace) when the timer has been disposed; otherwise
            // use Change in-place to match the pre-refactor cadence.
            _inner.Change(dueTime, period);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _inner?.Dispose();
            _inner = null;
        }
    }

    private sealed class PeriodicTimerWrapper : IPeriodicTimer
    {
        private readonly System.Threading.PeriodicTimer _inner;

        public PeriodicTimerWrapper(System.Threading.PeriodicTimer inner)
        {
            _inner = inner;
        }

        public Task<bool> WaitForNextTickAsync(CancellationToken cancellationToken = default)
            => _inner.WaitForNextTickAsync(cancellationToken).AsTask();

        public ValueTask DisposeAsync()
        {
            _inner.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}