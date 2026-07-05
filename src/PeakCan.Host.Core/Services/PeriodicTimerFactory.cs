using System.Threading;

namespace PeakCan.Host.Core.Services;

/// <summary>
/// v3.5.2 PATCH: production <see cref="ITimerFactory"/> backed by
/// <see cref="System.Threading.PeriodicTimer"/>. Registered as a
/// singleton in <c>AppHostBuilder</c>; consumers receive it through
/// DI and call <see cref="CreateTimer(TimeSpan)"/> from their
/// <c>ExecuteAsync</c>.
/// <para>
/// v3.5.4 PATCH: kept for the IPeriodicTimer-only consumers
/// (RecordService / StatisticsService / TraceService default ctors)
/// — those services don't need a cyclic timer. The
/// <see cref="CyclicTimerFactory"/> class implements both shapes and
/// is the registered singleton in DI; this class is preserved so the
/// default-ctor path of the periodic-timer services compiles without
/// a sweeping refactor. <see cref="CreateCyclicTimer"/> throws because
/// no caller of this class uses it.
/// </para>
/// </summary>
public sealed class PeriodicTimerFactory : ITimerFactory
{
    public IPeriodicTimer CreateTimer(TimeSpan period)
        => new PeriodicTimerWrapper(new System.Threading.PeriodicTimer(period));

    /// <summary>
    /// v3.5.4 PATCH: this factory does not produce cyclic timers.
    /// Callers needing <see cref="ICyclicTimer"/> should depend on
    /// <see cref="CyclicTimerFactory"/> instead.
    /// </summary>
    public ICyclicTimer CreateCyclicTimer(Action<object?> tickCallback, object? state, TimeSpan period)
        => throw new NotSupportedException(
            "PeriodicTimerFactory only produces IPeriodicTimer; use CyclicTimerFactory for ICyclicTimer.");

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
