using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core;

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
    private Timer? _timer;
    private CanFrame _frame;
    private TimeSpan _interval;
    private long _generation;
    private long _sendSuccessCount;
    private long _sendFailureCount;
    private bool _isRunning;

    /// <summary>True when the cyclic send timer is active.</summary>
    public bool IsRunning
    {
        get { lock (this) return _isRunning; }
    }

    /// <summary>
    /// Total frames sent since the last <see cref="Start"/> (success + failure).
    /// Retained for backward compatibility; new consumers should prefer
    /// <see cref="SuccessCount"/> + <see cref="FailureCount"/> so the UI can
    /// report the two outcomes separately.
    /// </summary>
    [Obsolete("Use SuccessCount + FailureCount. v1.2.12 split the mixed counter; remove in v1.2.13.")]
    public long SendCount => Interlocked.Read(ref _sendSuccessCount) + Interlocked.Read(ref _sendFailureCount);

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
    {
        _sendService = sendService ?? throw new ArgumentNullException(nameof(sendService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
            gen = ++_generation;
            _timer = new Timer(OnTimerTick, gen, interval, interval);
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
        LogCyclicStopped(_logger, SuccessCount);
    }

    private async void OnTimerTick(object? state)
    {
        CanFrame frame;
        TimeSpan interval;
        long generation;
        lock (this)
        {
            if (!_isRunning) return;
            frame = _frame;
            interval = _interval;
            generation = _generation;
        }
        // Stale-timer drop: if this Timer was disposed (Start re-entered)
        // its captured generation no longer matches the service's. Bail
        // before touching SendAsync.
        if (state is long tickGen && tickGen != generation) return;

        try
        {
            var result = await _sendService.SendAsync(frame).ConfigureAwait(false);
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
