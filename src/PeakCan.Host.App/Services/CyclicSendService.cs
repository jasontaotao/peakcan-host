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
/// The frame and interval are immutable after <see cref="Start"/> — to
/// change them, call <see cref="Stop"/> then <see cref="Start"/> with
/// new parameters.
/// </para>
/// </summary>
public sealed partial class CyclicSendService : IDisposable
{
    private readonly SendService _sendService;
    private readonly ILogger<CyclicSendService> _logger;
    private Timer? _timer;
    private CanFrame _frame;
    private TimeSpan _interval;
    private long _sendCount;
    private bool _isRunning;

    /// <summary>True when the cyclic send timer is active.</summary>
    public bool IsRunning
    {
        get { lock (this) return _isRunning; }
    }

    /// <summary>Number of frames sent since the last <see cref="Start"/>.</summary>
    public long SendCount => Interlocked.Read(ref _sendCount);

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
        lock (this)
        {
            if (_isRunning) StopInner();
            _frame = frame;
            _interval = interval;
            _sendCount = 0;
            _isRunning = true;
            _timer = new Timer(OnTimerTick, null, interval, interval);
            LogCyclicStarted(_logger, frame.Id, interval.TotalMilliseconds);
        }
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
        _timer?.Dispose();
        _timer = null;
        LogCyclicStopped(_logger, _sendCount);
    }

    private async void OnTimerTick(object? state)
    {
        if (!_isRunning) return;
        try
        {
            var result = await _sendService.SendAsync(_frame).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                Interlocked.Increment(ref _sendCount);
            }
            else
            {
                // Don't spam logs — only log every 100th failure.
                var count = Interlocked.Increment(ref _sendCount);
                if (count % 100 == 0)
                {
                    LogCyclicSendFailed(_logger, _frame.Id, result.Error!.Code, result.Error.Message);
                }
            }
        }
        catch (Exception ex)
        {
            LogCyclicSendThrew(_logger, _frame.Id, ex);
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
