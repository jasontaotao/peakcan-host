namespace PeakCan.Host.Core.Services;

/// <summary>
/// v3.5.2 PATCH: abstraction over periodic timer creation so that
/// <see cref="Microsoft.Extensions.Hosting.BackgroundService"/>-derived
/// classes (RecordService, StatisticsService) can be tested without
/// depending on a real <see cref="System.Threading.PeriodicTimer"/>'s
/// wall-clock wakeup schedule. Production DI registers
/// <see cref="PeriodicTimerFactory"/> (real <c>PeriodicTimer</c>); test
/// ctor wires <c>FakeTimerFactory</c> (in the App.Tests assembly) so
/// ticks fire on demand with zero <c>Task.Delay</c> in the test body.
/// </summary>
public interface ITimerFactory
{
    /// <summary>
    /// Create a periodic timer that yields the next tick after
    /// <paramref name="period"/> elapses. Implementations may
    /// <i>actually wait</i> the period (production) or
    /// <i>immediately</i> yield when test code calls
    /// <c>Fire()</c> (test double).
    /// </summary>
    IPeriodicTimer CreateTimer(TimeSpan period);
}

/// <summary>
/// v3.5.2 PATCH: an awaitable periodic-timer seam. Mirrors the
/// subset of <see cref="System.Threading.PeriodicTimer"/> we use:
/// <see cref="WaitForNextTickAsync"/> and <see cref="IAsyncDisposable"/>.
/// Keep the surface intentionally tiny — adding methods here ripples
/// to both the production wrapper and the fake.
/// </summary>
public interface IPeriodicTimer : IAsyncDisposable
{
    /// <summary>
    /// Wait for the next tick. Production wraps
    /// <see cref="System.Threading.PeriodicTimer.WaitForNextTickAsync(CancellationToken)"/>;
    /// the fake returns a Task that completes when the test calls
    /// <c>Fire()</c>.
    /// </summary>
    /// <returns>
    /// <c>true</c> when a tick fires; <c>false</c> when the timer
    /// stopped without ever firing (production only, on
    /// Dispose-before-first-tick).
    /// </returns>
    Task<bool> WaitForNextTickAsync(CancellationToken cancellationToken = default);
}
