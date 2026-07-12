using Microsoft.Extensions.Logging;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;
using PeakCan.Host.Core.Services;

namespace PeakCan.Host.App.Services;

/// <summary>
/// v1.5.1 PATCH Item 2 (Periodic DBC send): periodically encodes a
/// <see cref="Message"/> via <see cref="DbcEncodeService"/> and dispatches
/// the resulting <see cref="CanFrame"/> through <see cref="SendService"/>.
/// The signal-values source is a <c>Func&lt;(Message, IReadOnlyDictionary&lt;string, double&gt;)&gt;</c>
/// invoked on each tick so user edits to the signal DataGrid flow into the
/// periodic send path naturally (Decision 8).
/// <para>
/// <b>Thread-safety:</b> the timer callback runs on a ThreadPool thread.
/// All mutable state (<c>_isRunning</c>, <c>_frameProvider</c>,
/// <c>_interval</c>, <c>_generation</c>, <c>_capturedMessageId</c>) is read
/// under <c>lock(this)</c> at the top of <see cref="OnTimerTick"/> so
/// <see cref="Stop"/> and a re-entrant <see cref="Start"/> are observed
/// atomically. The encode + send runs OUTSIDE the lock (downstream locks
/// are timer-thread-safe; <see cref="SendService.SendAsync"/> uses
/// <see cref="System.Threading.Volatile.Read{T}(ref T)"/> on the channel
/// ref).
/// </para>
/// <para>
/// <b>Per-tick semantics:</b>
/// <list type="number">
///   <item>Read state snapshot under lock (frameProvider + interval + generation + capturedMessageId).</item>
///   <item>Stale-timer drop: callback generation mismatch → bail before touching state.</item>
///   <item>Invoke frameProvider → (Message, values). Compare Message.Id with captured id; mismatch → stop + FailureCount++ (one-time leak surface, Decision 9).</item>
///   <item>Encode via <see cref="DbcEncodeService"/>; <see cref="DbcSignalEncodeException"/> → FailureCount++ + log every 100th (Decision 10).</item>
///   <item>Construct <see cref="CanFrame"/> from Message.Id (with bit-31 IDE check; mirror <c>DbcSendViewModel.SendAsync</c>).</item>
///   <item>Send via <see cref="SendService.SendAsync"/>; result.IsSuccess → SuccessCount++, else FailureCount++ + log every 100th.</item>
/// </list>
/// </para>
/// <para>
/// <b>Independence from <see cref="CyclicSendService"/>:</b> per Decision 7,
/// this service intentionally duplicates the lock + generation + timer
/// pattern. Sharing a base class would introduce unnecessary abstraction
/// and couple two unrelated race-fix invariants that v1.2.12 PATCH Item 10
/// stabilized separately. Each service carries its own race-test suite
/// (<c>CyclicDbcSendServiceRaceTests</c> + <c>CyclicSendServiceRaceTests</c>).
/// </para>
/// <para>
/// v3.5.4 PATCH: the production <see cref="System.Threading.Timer"/> is
/// now obtained from an <see cref="ITimerFactory"/> via
/// <see cref="ICyclicTimer"/>. Production DI wires
/// <c>CyclicTimerFactory</c>; unit tests inject a
/// <c>FakeTimerFactory</c> and call
/// <c>FakeCyclicTimer.Fire()</c> / <c>Fire(count)</c> to advance ticks
/// deterministically, replacing the <c>Task.Delay</c>-based wait
/// patterns in <c>CyclicDbcSendServiceRaceTests</c> that previously
/// relied on wall-clock luck. The race-fix invariants are unchanged.
/// </para>
/// </summary>
public sealed partial class CyclicDbcSendService : ICyclicDbcSendService, IDisposable
{
    private readonly DbcEncodeService _encoder;
    private readonly SendService _sendService;
    private readonly ILogger<CyclicDbcSendService> _logger;
    private readonly ITimerFactory _timerFactory;
    private ICyclicTimer? _timer;
    private Func<(Message message, IReadOnlyDictionary<string, double> values)>? _frameProvider;
    private TimeSpan _interval;
    private long _generation;
    private long _sendSuccessCount;
    private long _sendFailureCount;
    private bool _isRunning;
    // v1.6.2 PATCH Item 1b: per-Start CancellationTokenSource. Stop() cancels
    // this source so in-flight SendAsync receives a cancelled CT — true abort
    // of the channel write, not just "prevent new ticks". Disposed in Start
    // (when replaced) and in Dispose (final cleanup).
    private CancellationTokenSource? _cts;
    // Captured on first tick; subsequent ticks compare provider's current
    // Message.Id to detect "user switched message mid-run" (Decision 9).
    private uint? _capturedMessageId;

    /// <summary>True when the cyclic DBC send timer is active.</summary>
    // === Flow B methods moved to CyclicDbcSendService/Cycling.partial.cs (W23 Task 2) ===

    public void Dispose()
    {
        lock (this)
        {
            StopInner();
            // v1.6.2 PATCH Item 1b: dispose the CTS to release its internal
            // ManualResetEvent handle. Without this, repeated Start/Stop
            // cycles leak native resources.
            _cts?.Dispose();
            _cts = null;
        }
    }

}
