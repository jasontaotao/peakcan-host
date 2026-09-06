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
/// <para>
/// v3.5.4 PATCH: extended with <see cref="CreateCyclicTimer"/> for
/// <see cref="ICyclicTimer"/>-shaped consumers (CyclicSendService,
/// CyclicDbcSendService) that wrap
/// <see cref="System.Threading.Timer"/> rather than
/// <see cref="System.Threading.PeriodicTimer"/>. Both abstractions
/// coexist intentionally — they cover distinct timer shapes (awaitable
/// loop vs callback-driven) and unifying them would force one shape to
/// simulate the other.
/// </para>
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

    /// <summary>
    /// v3.5.4 PATCH: create a callback-driven cyclic timer. Production
    /// wraps <see cref="System.Threading.Timer"/> (fires
    /// <see cref="ICyclicTimer.TickCallback"/> on a ThreadPool thread
    /// every <paramref name="period"/>); the test fake fires on demand
    /// via <c>Fire()</c> / <c>Fire(count)</c>. Use this shape for
    /// consumers that register a callback at construction time and
    /// re-arm via <see cref="ICyclicTimer.Change"/>, rather than the
    /// awaitable loop pattern of <see cref="CreateTimer"/>.
    /// </summary>
    ICyclicTimer CreateCyclicTimer(
        Action<object?> tickCallback,
        object? state,
        TimeSpan period);
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

/// <summary>
/// v3.5.4 PATCH: a callback-driven cyclic-timer seam. Mirrors the
/// subset of <see cref="System.Threading.Timer"/> the cyclic-send
/// services use: a callback is wired at construction time (passed to
/// the factory); <see cref="Change"/> re-arms the timer (mirrors
/// <see cref="System.Threading.Timer.Change(TimeSpan, TimeSpan)"/>);
/// <see cref="IDisposable.Dispose"/> stops it. Production wraps a real
/// <see cref="System.Threading.Timer"/>; the test fake fires
/// <c>TickCallback</c> on demand when test code calls
/// <c>Fire()</c> or <c>Fire(count)</c>.
/// <para>
/// The factory returns a fully-configured timer (callback + state +
/// initial period) so consumers do not need a separate wiring step;
/// <see cref="Change"/> handles re-arm on Start re-entry, exactly as
/// <c>Timer.Change</c> does in the pre-refactor code.
/// </para>
/// </summary>
public interface ICyclicTimer : IDisposable
{
    /// <summary>
    /// Callback fired on each tick. Production runs it on a ThreadPool
    /// thread (via <see cref="System.Threading.Timer"/>); the fake runs
    /// it on the calling thread (test body). State captured at
    /// construction is passed back to the callback exactly like
    /// <see cref="System.Threading.Timer"/>.
    /// </summary>
    Action<object?> TickCallback { get; }

    /// <summary>State object passed to <see cref="TickCallback"/> on each tick.</summary>
    object? State { get; }

    /// <summary>
    /// Re-arm the timer with a new <paramref name="dueTime"/> and
    /// <paramref name="period"/>. Mirrors
    /// <see cref="System.Threading.Timer.Change(TimeSpan, TimeSpan)"/>:
    /// subsequent ticks fire after <paramref name="dueTime"/> and then
    /// every <paramref name="period"/>. A no-op on the fake (the fake
    /// does not consult cadence — tests fire ticks manually).
    /// </summary>
    void Change(TimeSpan dueTime, TimeSpan period);
}