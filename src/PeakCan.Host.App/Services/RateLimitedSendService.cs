using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core;

namespace PeakCan.Host.App.Services;

/// <summary>
/// v1.6.5 PATCH Item 1 — Send-rate-limiting decorator for
/// <see cref="SendService"/>. Applies a token-bucket rate limit:
/// refill rate = <c>MaxFramesPerSecond</c> tokens per second,
/// burst capacity = <c>MaxFramesPerSecond</c> tokens. Frames are
/// rejected with a failed <see cref="Result{T}"/> carrying
/// <see cref="ErrorCode.HardwareBusy"/> when the bucket is empty.
/// <para>
/// <b>Default policy is opt-in:</b> when
/// <c>MaxFramesPerSecond &lt;= 0</c> the decorator applies no rate
/// limit and forwards every <c>SendAsync</c> call unchanged to the
/// inner <see cref="SendService"/>. Enable by setting
/// <c>Send:MaxFramesPerSecond</c> in <c>appsettings.json</c> to a
/// positive integer (e.g. <c>1000</c>).
/// </para>
/// <para>
/// <b>Non-blocking:</b> per the existing <see cref="SendService"/>
/// XML doc — "does not retry, does not enqueue, does not block" —
/// the decorator rejects immediately when the bucket is empty. It
/// does not block the caller waiting for a token.
/// </para>
/// <para>
/// <b>Caller identification:</b> the decorator is registered as
/// <see cref="SendService"/> at the DI seam. UI callers (CanApi,
/// SendViewModel, DbcSendViewModel, CyclicSendService,
/// CyclicDbcSendService) resolve <see cref="SendService"/> and
/// receive this wrapper via C# polymorphism. Callers with
/// rate-unfriendly semantics (Replay timeline + IsoTp transport)
/// resolve the raw <see cref="Composition.CoreSendService"/> type
/// and bypass the rate gate. See
/// <c>docs/superpowers/specs/2026-06-30-v1-6-5-patch-design.md</c>
/// for the full architecture.
/// </para>
/// <para>
/// <b>Thread safety:</b> token-bucket state
/// (<c>_tokens</c> + <c>_lastRefillTimestamp</c>) is protected by
/// <c>lock(this)</c>. The lock is held only for bucket arithmetic;
/// the inner <see cref="SendService.SendAsync"/> call is dispatched
/// after the lock is released so a slow channel cannot block other
/// callers' token consumption. <c>RejectedFrameCount</c> uses
/// <see cref="Interlocked"/> for lock-free reads.
/// </para>
/// <para>
/// <b>Clock source:</b> <see cref="Stopwatch.GetTimestamp"/> —
/// monotonic, immune to system clock adjustments. Never
/// <see cref="DateTime.UtcNow"/>.
/// </para>
/// </summary>
internal sealed partial class RateLimitedSendService : SendService
{
    private readonly SendService _inner;
    private readonly ILogger<RateLimitedSendService> _logger;
    private readonly int _maxFramesPerSecond;
    private readonly double _refillTokensPerTick;
    private readonly long _stopwatchFrequency;

    /// <summary>
    /// Current token count. Initialized to <c>MaxFramesPerSecond</c>
    /// (full burst) or <see cref="double.PositiveInfinity"/> when
    /// the rate limit is disabled (<c>MaxFramesPerSecond &lt;= 0</c>).
    /// </summary>
    private double _tokens;

    private long _lastRefillTimestamp;
    private long _rejectedFrameCount;

    /// <summary>
    /// Last (monotonic) timestamp at which a rate-limit rejection
    /// was logged. Used to throttle the rejection log to ~1 Hz so
    /// high-frequency reject storms do not flood the log.
    /// </summary>
    private long _lastLogTimestamp;

    /// <summary>
    /// Cumulative count of frames rejected by the rate limit. Exposed
    /// for tests + future UI surface; not wired to UI in v1.6.5.
    /// Reads are lock-free (<see cref="Interlocked.Read"/>).
    /// </summary>
    public long RejectedFrameCount => Interlocked.Read(ref _rejectedFrameCount);

    /// <summary>
    /// Construct the decorator.
    /// </summary>
    /// <param name="inner">The underlying <see cref="SendService"/> to
    /// forward accepted frames to. Must not be <c>null</c>.</param>
    /// <param name="maxFramesPerSecond">Token-bucket refill rate AND
    /// burst capacity. <c>&lt;= 0</c> disables the rate limit entirely.</param>
    /// <param name="logger">Logger for rejection events.</param>
    public RateLimitedSendService(
        SendService inner,
        int maxFramesPerSecond,
        ILogger<RateLimitedSendService> logger)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _maxFramesPerSecond = maxFramesPerSecond;
        _stopwatchFrequency = Stopwatch.Frequency;
        _refillTokensPerTick = maxFramesPerSecond > 0
            ? maxFramesPerSecond / (double)_stopwatchFrequency
            : 0.0;
        _tokens = maxFramesPerSecond > 0 ? maxFramesPerSecond : double.PositiveInfinity;
        _lastRefillTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Apply the token-bucket gate. Forward to <see cref="_inner"/>
    /// if a token is available; otherwise return a failed
    /// <see cref="Result{T}"/> with <see cref="ErrorCode.HardwareBusy"/>.
    /// The caller's <see cref="CancellationToken"/> is preserved
    /// unchanged on both paths.
    /// </summary>
    public override ValueTask<Result<Unit>> SendAsync(CanFrame frame, CancellationToken ct = default)
    {
        // Opt-out: unlimited bypass when policy is disabled.
        if (_maxFramesPerSecond <= 0)
        {
            return _inner.SendAsync(frame, ct);
        }

        lock (this)
        {
            var now = Stopwatch.GetTimestamp();
            var elapsed = now - _lastRefillTimestamp;
            if (elapsed > 0)
            {
                _tokens = Math.Min(
                    _tokens + elapsed * _refillTokensPerTick,
                    _maxFramesPerSecond);
                _lastRefillTimestamp = now;
            }

            if (_tokens >= 1.0)
            {
                _tokens -= 1.0;
                // Fall through to delegate after the lock is released.
            }
            else
            {
                Interlocked.Increment(ref _rejectedFrameCount);

                // 1 Hz log throttle: only emit if at least one full
                // second has elapsed since the last reject log. High-
                // frequency reject storms are normal user-visible
                // behavior, not errors; Information level is correct.
                if (now - _lastLogTimestamp >= _stopwatchFrequency)
                {
                    _lastLogTimestamp = now;
                    LogRateLimited(_logger, frame.Id.Raw, _maxFramesPerSecond);
                }

                return ValueTask.FromResult(Result<Unit>.Fail(
                    ErrorCode.HardwareBusy,
                    $"rate limit ({_maxFramesPerSecond} fps); frame 0x{frame.Id.Raw:X} rejected"));
            }
        }

        // Lock scope ends here. Dispatch inner after release so a slow
        // channel cannot block other callers' token consumption.
        return _inner.SendAsync(frame, ct);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "RateLimitedSendService rejected frame 0x{FrameId:X} (max {MaxFps:F0} fps)")]
    private static partial void LogRateLimited(ILogger logger, uint frameId, int maxFps);
}
