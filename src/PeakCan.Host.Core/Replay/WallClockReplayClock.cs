namespace PeakCan.HIL.Core.Replay;

/// <summary>
/// Default <see cref="IReplayClock"/> implementation that uses real
/// wall-clock time (<see cref="DateTime.UtcNow"/>), real
/// <see cref="System.Threading.Timer"/>, and real
/// <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.
/// Registered as a DI singleton in production.
/// </summary>
public sealed class WallClockReplayClock : IReplayClock
{
    public DateTime Now => DateTime.UtcNow;

    public Task Delay(TimeSpan delay, CancellationToken cancellationToken = default)
        => Task.Delay(delay, cancellationToken);

    public IDisposable CreateTimer(TimerCallback callback, object? state,
        TimeSpan dueTime, TimeSpan period)
        => new Timer(callback, state, dueTime, period);
}