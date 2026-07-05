using System.Threading;

namespace PeakCan.Host.Core.Services;

/// <summary>
/// v3.5.2 PATCH: production <see cref="ITimerFactory"/> backed by
/// <see cref="System.Threading.PeriodicTimer"/>. Registered as a
/// singleton in <c>AppHostBuilder</c>; consumers receive it through
/// DI and call <see cref="CreateTimer(TimeSpan)"/> from their
/// <c>ExecuteAsync</c>.
/// </summary>
public sealed class PeriodicTimerFactory : ITimerFactory
{
    public IPeriodicTimer CreateTimer(TimeSpan period)
        => new PeriodicTimerWrapper(new System.Threading.PeriodicTimer(period));

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
