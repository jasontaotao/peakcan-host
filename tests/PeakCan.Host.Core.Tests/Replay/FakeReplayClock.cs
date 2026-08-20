using System.Threading;
using PeakCan.HIL.Core.Replay;

namespace PeakCan.HIL.Core.Tests.Replay;

/// <summary>
/// Deterministic <see cref="IReplayClock"/> for tests. Does NOT consume
/// wall-clock time — <see cref="Delay"/> completes synchronously and
/// advances <see cref="Now"/> by the requested duration. Use
/// <see cref="Advance"/> to manually advance time or <see cref="Delay"/>
/// to simulate an async wait.
/// <para>
/// The timer created by <see cref="CreateTimer"/> is controlled via
/// <see cref="TickOnce"/> / <see cref="TickRepeated"/> — the callback
/// only fires when the test explicitly advances the clock.
/// </para>
/// </summary>
public sealed class FakeReplayClock : IReplayClock
{
    private DateTime _now;
    private TimerCallback? _timerCallback;
    private object? _timerState;
    private TimeSpan _timerPeriod;

    public FakeReplayClock()
    {
        _now = DateTime.UtcNow;
    }

    public DateTime Now => _now;

    public Task Delay(TimeSpan delay, CancellationToken cancellationToken = default)
    {
        _now += delay;
        return Task.CompletedTask;
    }

    public IDisposable CreateTimer(TimerCallback callback, object? state,
        TimeSpan dueTime, TimeSpan period)
    {
        _timerCallback = callback;
        _timerState = state;
        _timerPeriod = period;
        return new FakeTimerDisposable(() =>
        {
            _timerCallback = null;
            _timerState = null;
        });
    }

    /// <summary>
    /// Advance <see cref="Now"/> by the specified duration. Does NOT
    /// fire the timer callback. Use this to manually move the clock
    /// without simulating a timer tick.
    /// </summary>
    public void Advance(TimeSpan duration)
    {
        _now += duration;
    }

    /// <summary>
    /// Fire the timer callback once (if one was created) and advance
    /// the clock by the timer's period. Simulates one full timer tick.
    /// </summary>
    public void TickOnce()
    {
        if (_timerCallback is not null)
        {
            _timerCallback(_timerState);
        }
        _now += _timerPeriod;
    }

    /// <summary>
    /// Fire the timer callback <paramref name="count"/> times, advancing
    /// the clock by the timer's period after each tick.
    /// </summary>
    public void TickRepeated(int count)
    {
        for (var i = 0; i < count; i++)
        {
            TickOnce();
        }
    }

    private sealed class FakeTimerDisposable : IDisposable
    {
        private readonly Action _onDispose;
        public FakeTimerDisposable(Action onDispose) => _onDispose = onDispose;
        public void Dispose() => _onDispose();
    }
}