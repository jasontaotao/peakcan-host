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

    private async void OnTimerTick(object? state)
    {
        Func<(Message message, IReadOnlyDictionary<string, double> values)>? provider;
        TimeSpan interval;
        long generation;
        CancellationToken ct;
        lock (this)
        {
            if (!_isRunning) return;
            provider = _frameProvider;
            interval = _interval;
            generation = _generation;
            // v1.6.2 PATCH Item 1b: snapshot CTS.Token under the same lock
            // as _isRunning + _frameProvider + _generation so the tick sees
            // a coherent view. Stop() flips _isRunning + cancels _cts under
            // the same lock, so this snapshot is consistent with the
            // isRunning snapshot above.
            ct = _cts?.Token ?? CancellationToken.None;
        }
        // Stale-timer drop: if this Timer was disposed (Start re-entered)
        // its captured generation no longer matches the service's. Bail
        // before touching state.
        if (state is long tickGen && tickGen != generation) return;
        if (provider is null) return;

        (Message message, IReadOnlyDictionary<string, double> values) snapshot;
        try
        {
            snapshot = provider();
        }
        catch (Exception ex)
        {
            var count = Interlocked.Increment(ref _sendFailureCount);
            if (count % 100 == 0)
            {
                LogCyclicDbcProviderThrew(_logger, ex);
            }
            return;
        }

        // Decision 9: detect "user switched message mid-run". On the
        // first tick, capture the baseline. On subsequent ticks, compare
        // the provider's current Message.Id to the captured one. A
        // mismatch means the user changed the DBC message dropdown while
        // periodic send was active; stop + record one failure so the
        // silence is observable in the UI counter.
        bool messageChanged = false;
        lock (this)
        {
            if (_capturedMessageId is null)
            {
                _capturedMessageId = snapshot.message.Id;
            }
            else if (_capturedMessageId.Value != snapshot.message.Id)
            {
                messageChanged = true;
            }
        }
        if (messageChanged)
        {
            Stop();
            Interlocked.Increment(ref _sendFailureCount);
            LogCyclicDbcMessageChanged(_logger, _capturedMessageId, snapshot.message.Id);
            return;
        }

        // v1.6.1 PATCH Item 1: defensive _isRunning re-check after the
        // Message.Id lock and before encode. Closes the race window
        // where Stop() can be called between the snapshot lock and
        // encode, allowing an in-flight tick to complete even though
        // Stop was requested. The re-check is cheap (< 1μs) and
        // reuses the existing lock(this) pattern.
        lock (this)
        {
            if (!_isRunning) return;
        }

        byte[] payload;
        try
        {
            payload = _encoder.Encode(snapshot.message, snapshot.values);
        }
        catch (DbcSignalEncodeException ex)
        {
            // Decision 10: encode failure is per-tick; counter + log
            // every 100th to avoid log spam. The exception message
            // identifies the offending signal; we log the full message so
            // engineers can correlate the failure to the input data.
            var count = Interlocked.Increment(ref _sendFailureCount);
            if (count % 100 == 0)
            {
                LogCyclicDbcEncodeThrew(_logger, snapshot.message.Id, ex);
            }
            return;
        }

        // Mirror DbcSendViewModel.SendAsync: DBC messages use the PEAK
        // convention bit 31 set ⇒ Extended, clear ⇒ Standard. The CanId
        // ctor validates bit-width, so route the right format to avoid
        // ArgumentOutOfRangeException on 11-bit IDs.
        var id = snapshot.message.Id;
        var isExtended = (id & 0x80000000u) != 0u;
        var raw = isExtended ? (id & 0x1FFFFFFFu) : (id & 0x7FFu);
        var canId = new CanId(raw, isExtended ? FrameFormat.Extended : FrameFormat.Standard);
        var frame = new CanFrame(canId, payload, FrameFlags.None, ChannelId.None, default);

        // v1.6.1 PATCH Item 1: defensive _isRunning re-check after
        // encode and before send. Closes the second race window
        // (encode → send await) where Stop() can be called while a
        // tick is mid-flight. The first re-check covers the snapshot
        // → encode window; this one covers encode → send.
        lock (this)
        {
            if (!_isRunning) return;
        }

        try
        {
            // v1.6.2 PATCH Item 1b: pass CT so Stop() can abort the in-flight
            // channel write. _sendService.SendAsync forwards ct to
            // ch.WriteAsync(frame, ct) which honors the token.
            var result = await _sendService.SendAsync(frame, ct).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                Interlocked.Increment(ref _sendSuccessCount);
            }
            else
            {
                var count = Interlocked.Increment(ref _sendFailureCount);
                if (count % 100 == 0)
                {
                    LogCyclicDbcSendFailed(_logger, frame.Id, result.Error!.Code, result.Error.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // v1.6.2 PATCH Item 1b: expected on Stop(). async void timer
            // callback would crash the process if OCE propagated uncaught.
            // Do NOT increment FailureCount — Stop is user-initiated, not
            // a hardware failure.
        }
        catch (Exception ex)
        {
            var count = Interlocked.Increment(ref _sendFailureCount);
            if (count % 100 == 0)
            {
                LogCyclicDbcSendThrew(_logger, frame.Id, ex);
            }
        }
    }

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

    [LoggerMessage(Level = LogLevel.Information, Message = "Cyclic DBC send started every {Interval}ms")]
    private static partial void LogCyclicDbcStarted(ILogger logger, double interval);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cyclic DBC send stopped: {Count} frames sent")]
    private static partial void LogCyclicDbcStopped(ILogger logger, long count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cyclic DBC send message changed mid-run (was {OldId}, now {NewId}); auto-stopped")]
    private static partial void LogCyclicDbcMessageChanged(ILogger logger, uint? oldId, uint newId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cyclic DBC send failed for {Id}: {Code} {Message}")]
    private static partial void LogCyclicDbcSendFailed(ILogger logger, CanId id, ErrorCode code, string message);

    [LoggerMessage(Level = LogLevel.Error, Message = "Cyclic DBC send threw for {Id}")]
    private static partial void LogCyclicDbcSendThrew(ILogger logger, CanId id, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Cyclic DBC encode threw for message {MessageId}")]
    private static partial void LogCyclicDbcEncodeThrew(ILogger logger, uint messageId, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Cyclic DBC frame provider threw")]
    private static partial void LogCyclicDbcProviderThrew(ILogger logger, Exception ex);
}
