using System.Threading;

namespace PeakCan.HIL.Core.Replay;

/// <summary>
/// Abstraction over wall-clock time, timers, and asynchronous delays,
/// allowing deterministic control of time in tests. Production code uses
/// <see cref="WallClockReplayClock"/>; tests use a fake implementation.
/// </summary>
public interface IReplayClock
{
    /// <summary>Current point in time. Used to compute playback position.</summary>
    DateTime Now { get; }

    /// <summary>
    /// Returns a task that completes after the specified duration.
    /// Production: delegates to <c>Task.Delay</c>.
    /// Test: completes synchronously (no wall-clock time consumed).
    /// </summary>
    Task Delay(TimeSpan delay, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a timer that fires <paramref name="callback"/> after
    /// <paramref name="dueTime"/> and then every <paramref name="period"/>.
    /// Production: wraps <see cref="System.Threading.Timer"/>.
    /// Test: returns a fake timer that does NOT fire based on wall-clock time.
    /// </summary>
    IDisposable CreateTimer(TimerCallback callback, object? state,
        TimeSpan dueTime, TimeSpan period);
}