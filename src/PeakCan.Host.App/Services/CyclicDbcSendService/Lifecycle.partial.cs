using Microsoft.Extensions.Logging;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;
using PeakCan.Host.Core.Services;

namespace PeakCan.Host.App.Services;

public sealed partial class CyclicDbcSendService
{
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
    /// Number of frames that failed to encode + frames the channel
    /// reported as failed since the last <see cref="Start"/>. Both
    /// failure modes share a single counter (per Decision 10).
    /// </summary>
    public long FailureCount => Interlocked.Read(ref _sendFailureCount);

    public CyclicDbcSendService(
        DbcEncodeService encoder,
        SendService sendService,
        ILogger<CyclicDbcSendService> logger)
        : this(encoder, sendService, logger, new CyclicTimerFactory())
    {
    }

    /// <summary>
    /// v3.5.4 PATCH: internal ctor lets unit tests inject an
    /// <see cref="ITimerFactory"/> (typically a <c>FakeTimerFactory</c>)
    /// so race tests can advance ticks deterministically via
    /// <c>FakeCyclicTimer.Fire()</c>. Production DI uses the public
    /// 3-arg ctor above, which constructs a real
    /// <c>CyclicTimerFactory</c> (backed by
    /// <see cref="System.Threading.Timer"/>). Mirrors the v3.5.2/v3.5.3
    /// dual-ctor pattern in RecordService / TraceService.
    /// </summary>
    internal CyclicDbcSendService(
        DbcEncodeService encoder,
        SendService sendService,
        ILogger<CyclicDbcSendService> logger,
        ITimerFactory timerFactory)
    {
        _encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
        _sendService = sendService ?? throw new ArgumentNullException(nameof(sendService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timerFactory = timerFactory ?? throw new ArgumentNullException(nameof(timerFactory));
    }

    /// <summary>
    /// Start periodic DBC transmission using <paramref name="frameProvider"/>
    /// at <paramref name="interval"/>. If already running, stops first
    /// (mirrors <see cref="CyclicSendService.Start"/>).
    /// </summary>
    public void Start(
        Func<(Message message, IReadOnlyDictionary<string, double> values)> frameProvider,
        TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(frameProvider);
        long gen;
        lock (this)
        {
            // StopInner under the same lock so an in-flight OnTimerTick
            // observes the _isRunning flip atomically with our state
            // updates.
            StopInner();
            _frameProvider = frameProvider;
            _interval = interval;
            _isRunning = true;
            // _capturedMessageId will be captured on the first tick —
            // before that we have no baseline. The provider's first
            // invocation always establishes the captured id.
            _capturedMessageId = null;
            // Reset split counters so "since the last Start" remains the
            // documented contract (mirror CyclicSendService).
            Interlocked.Exchange(ref _sendSuccessCount, 0);
            Interlocked.Exchange(ref _sendFailureCount, 0);
            // v1.6.2 PATCH Item 1b: dispose previous CTS (if any) and create
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
        LogCyclicDbcStarted(_logger, interval.TotalMilliseconds);
    }

    /// <summary>Stop cyclic DBC transmission. Idempotent.</summary>
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
        // the lock check observes a mismatch and bails before encode/send.
        _generation++;
        _timer?.Dispose();
        _timer = null;
        // v1.6.2 PATCH Item 1b: cancel in-flight SendAsync. The CTS was
        // snapshotted by OnTimerTick under the lock above; cancelling here
        // propagates through _sendService.SendAsync(frame, ct) into
        // ch.WriteAsync(frame, ct) which honors the token.
        _cts?.Cancel();
        LogCyclicDbcStopped(_logger, SuccessCount);
    }
}