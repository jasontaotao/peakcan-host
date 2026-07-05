using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Services;

namespace PeakCan.Host.App.Services;

/// <summary>
/// Periodically transmits a configured <see cref="CanFrame"/> on the
/// active channel. The send interval is configurable (default 100 ms);
/// the service starts and stops via <see cref="Start"/> / <see cref="Stop"/>.
/// <para>
/// <b>Thread-safety:</b> the timer callback runs on a ThreadPool thread;
/// <see cref="SendService.SendAsync"/> is thread-safe (it uses
/// <see cref="System.Threading.Volatile.Read{T}"/> on the channel ref).
/// All mutable state (<c>_isRunning</c>, <c>_frame</c>, <c>_interval</c>,
/// <c>_generation</c>) is read under <c>lock(this)</c> at the top of
/// <see cref="OnTimerTick"/> so that <see cref="Stop"/> and a re-entrant
/// <see cref="Start"/> are observed atomically. To change the frame or
/// interval, call <see cref="Stop"/> then <see cref="Start"/> with new
/// parameters.
/// </para>
/// <para>
/// v1.2.12 PATCH Item 10: each Timer carries its <c>generation</c> as
/// the callback state; callbacks with a stale generation are dropped,
/// which closes the Start re-entry window where an old Timer could
/// still be queued after <c>_timer.Dispose()</c>. The single
/// <c>_sendCount</c> counter was split into <c>SuccessCount</c> +
/// <c>FailureCount</c> so the UI no longer reports mixed success/failure
/// totals under the same "frames sent" label.
/// </para>
/// </summary>
public sealed partial class CyclicSendService : ICyclicSendService, IDisposable
{
    private readonly SendService _sendService;
    private readonly ILogger<CyclicSendService> _logger;
    private readonly ITimerFactory _timerFactory;
    private ICyclicTimer? _timer;
    private CanFrame _frame;
    private TimeSpan _interval;
    private long _generation;
    private long _sendSuccessCount;
    private long _sendFailureCount;
    private bool _isRunning;
    // v1.6.2 PATCH Item 1a: per-Start CancellationTokenSource. Stop() cancels
    // this source so in-flight SendAsync receives a cancelled CT — true abort
    // of the channel write, not just "prevent new ticks". Disposed in Start
    // (when replaced) and in Dispose (final cleanup).
    private CancellationTokenSource? _cts;

    /// <summary>True when the cyclic send timer is active.</summary>
    public bool IsRunning
    {
        get { lock (this) return _isRunning; }
    }

    /// <summary>
    /// Number of frames the channel reported as successfully transmitted
    /// since the last <see cref="Start"/>. Reset to 0 by each new
    /// <see cref="Start"/> call.
    /// </summary>
    public long SuccessCount => Interlocked.Read(ref _sendSuccessCount);

    /// <summary>
    /// Number of frames the channel reported as failed since the last
    /// <see cref="Start"/>. Reset to 0 by each new <see cref="Start"/> call.
    /// </summary>
    public long FailureCount => Interlocked.Read(ref _sendFailureCount);

    public CyclicSendService(SendService sendService, ILogger<CyclicSendService> logger)
        : this(sendService, logger, new CyclicTimerFactory())
    {
    }

    /// <summary>
    /// v3.5.4 PATCH: internal ctor lets unit tests inject an
    /// <see cref="ITimerFactory"/> (typically a <c>FakeTimerFactory</c>)
    /// so race tests can advance ticks deterministically via
    /// <c>FakeCyclicTimer.Fire()</c>. Production DI uses the public
    /// 2-arg ctor above, which constructs a real
    /// <c>CyclicTimerFactory</c> (backed by
    /// <see cref="System.Threading.Timer"/>). Mirrors the v3.5.2/v3.5.3
    /// dual-ctor pattern in RecordService / TraceService.
    /// </summary>
    internal CyclicSendService(
        SendService sendService,
        ILogger<CyclicSendService> logger,
        ITimerFactory timerFactory)
    {
        _sendService = sendService ?? throw new ArgumentNullException(nameof(sendService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timerFactory = timerFactory ?? throw new ArgumentNullException(nameof(timerFactory));
    }

    /// <summary>
    /// Start cyclic transmission of <paramref name="frame"/> at
    /// <paramref name="interval"/>. If already running, stops first.
    /// </summary>
    public void Start(CanFrame frame, TimeSpan interval)
    {
        long gen;
        lock (this)
        {
            // StopInner under the same lock so an in-flight OnTimerTick
            // observes the _isRunning flip atomically with our _frame /
            // _generation updates.
            StopInner();
            _frame = frame;
            _interval = interval;
            _isRunning = true;
            // v1.2.12 PATCH Item 10 (Review Cycle 1 I-1): reset the split
            // counters so "since the last Start" remains the documented
            // contract. Without this, a second Start would carry counts
            // from the previous cycle.
            Interlocked.Exchange(ref _sendSuccessCount, 0);
            Interlocked.Exchange(ref _sendFailureCount, 0);
            // v1.6.2 PATCH Item 1a: dispose previous CTS (if any) and create
            // a fresh one. Without this, a second Start would inherit a
            // cancelled token and every tick would throw OCE on SendAsync.
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            gen = ++_generation;
            // v3.5.4 PATCH: timer obtained from the factory. The fake
            // (FakeCyclicTimer) records the callback + state + period
            // but does not auto-fire; tests call Fire() to advance
            // deterministically.
            _timer = _timerFactory.CreateCyclicTimer(OnTimerTick, gen, interval);
        }
        LogCyclicStarted(_logger, frame.Id, interval.TotalMilliseconds);
    }

    /// <summary>Stop cyclic transmission. Idempotent.</summary>
    public void Stop()
    {
        lock (this)
        {
            StopInner();
        }
    }

    private void StopInner()
    {
        if (!_isRunning) return;
        _isRunning = false;
        // Bump generation so any in-flight OnTimerTick that already passed
        // the lock check observes a mismatch and bails before SendAsync.
        _generation++;
        _timer?.Dispose();
        _timer = null;
        // v1.6.2 PATCH Item 1a: cancel in-flight SendAsync. The CTS was
        // snapshotted by OnTimerTick under the lock above; cancelling here
        // propagates through _sendService.SendAsync(frame, ct) into
        // ch.WriteAsync(frame, ct) which honors the token.
        _cts?.Cancel();
        LogCyclicStopped(_logger, SuccessCount);
    }

    private async void OnTimerTick(object? state)
    {
        CanFrame frame;
        TimeSpan interval;
        long generation;
        CancellationToken ct;
        lock (this)
        {
            if (!_isRunning) return;
            frame = _frame;
            interval = _interval;
            generation = _generation;
            // v1.6.2 PATCH Item 1a: snapshot CTS.Token under the same lock
            // as _isRunning + _frame + _generation so the tick sees a
            // coherent view. If Start replaced _cts after the timer fired
            // but before we acquired the lock, the snapshot reflects the
            // current CTS — Stop on the new CTS will cancel this tick.
            ct = _cts?.Token ?? CancellationToken.None;
        }
        // Stale-timer drop: if this Timer was disposed (Start re-entered)
        // its captured generation no longer matches the service's. Bail
        // before touching SendAsync.
        if (state is long tickGen && tickGen != generation) return;

        try
        {
            // v1.6.2 PATCH Item 1a: pass CT so Stop() can abort the in-flight
            // channel write. _sendService.SendAsync forwards ct to
            // ch.WriteAsync(frame, ct) which honors the token.
            var result = await _sendService.SendAsync(frame, ct).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                Interlocked.Increment(ref _sendSuccessCount);
            }
            else
            {
                // Don't spam logs — only log every 100th failure.
                var count = Interlocked.Increment(ref _sendFailureCount);
                if (count % 100 == 0 && _logger is not null)
                {
                    LogCyclicSendFailed(_logger, frame.Id, result.Error!.Code, result.Error.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // v1.6.2 PATCH Item 1a: expected on Stop(). async void timer
            // callback would crash the process if OCE propagated uncaught.
            // Do NOT increment FailureCount — Stop is user-initiated, not
            // a hardware failure.
        }
        catch (Exception ex)
        {
            var count = Interlocked.Increment(ref _sendFailureCount);
            if (count % 100 == 0 && _logger is not null)
            {
                LogCyclicSendThrew(_logger, frame.Id, ex);
            }
        }
    }

    public void Dispose()
    {
        lock (this)
        {
            StopInner();
            // v1.6.2 PATCH Item 1a: dispose the CTS to release its internal
            // ManualResetEvent handle. Without this, repeated Start/Stop
            // cycles leak native resources.
            _cts?.Dispose();
            _cts = null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Cyclic send started: {Id} every {Interval}ms")]
    private static partial void LogCyclicStarted(ILogger logger, CanId id, double interval);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cyclic send stopped: {Count} frames sent")]
    private static partial void LogCyclicStopped(ILogger logger, long count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cyclic send failed for {Id}: {Code} {Message}")]
    private static partial void LogCyclicSendFailed(ILogger logger, CanId id, ErrorCode code, string message);

    [LoggerMessage(Level = LogLevel.Error, Message = "Cyclic send threw for {Id}")]
    private static partial void LogCyclicSendThrew(ILogger logger, CanId id, Exception ex);
}
