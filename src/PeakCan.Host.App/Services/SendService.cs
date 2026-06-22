using Microsoft.Extensions.Logging;
using PeakCan.Host.Core;
using PeakCan.Host.Infrastructure.Channel;

namespace PeakCan.Host.App.Services;

/// <summary>
/// Manual frame send entry point. The MVP holds a single
/// <see cref="ICanChannel"/> reference and forwards
/// <see cref="SendAsync"/> calls to it; multi-channel routing is a v1.1
/// concern (Task 15+ shell navigation will introduce a per-tab channel
/// picker).
/// <para>
/// The service is intentionally a thin passthrough. It does not retry,
/// does not enqueue, and does not block. The view-model layer
/// (<c>SendViewModel</c>) is responsible for translating the result into
/// a user-facing status string.
/// </para>
/// <para>
/// <b>Thread-safety on <see cref="ActiveChannel"/>:</b> the backing
/// field is mutated with <see cref="Interlocked.Exchange{T}(ref T, T)"/>
/// and read with <see cref="Volatile.Read{T}(ref T)"/> so that the
/// setter can be safely invoked from a background thread (e.g. a
/// Task 17 statistics pump) without tearing or missing the latest
/// value. Both sides still observe a coherent value, but ordering
/// across the field + an external action (e.g. Set + Use) is the
/// caller's responsibility.
/// </para>
/// </summary>
public partial class SendService
{
    private readonly ILogger<SendService> _logger;
    private ICanChannel? _activeChannel;

    /// <summary>
    /// The channel the next <see cref="SendAsync"/> will target.
    /// <c>null</c> means "no channel connected" — the next send returns
    /// a failed <see cref="Result{T}"/> with <see cref="ErrorCode.InvalidState"/>
    /// instead of throwing. Thread-safe via
    /// <see cref="Interlocked.Exchange{T}(ref T, T)"/>.
    /// </summary>
    public ICanChannel? ActiveChannel
    {
        get => Volatile.Read(ref _activeChannel);
        set
        {
            var prev = Interlocked.Exchange(ref _activeChannel, value);
            if (ReferenceEquals(prev, value))
            {
                return;
            }
            if (value is null)
            {
                LogActiveChannelCleared(_logger);
            }
            else
            {
                LogActiveChannelChanged(_logger, value.Id.Handle);
            }
        }
    }

    public SendService(ILogger<SendService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Transmit <paramref name="frame"/> on the <see cref="ActiveChannel"/>.
    /// Returns a failed <see cref="Result{T}"/> with
    /// <see cref="ErrorCode.InvalidState"/> when no channel is connected.
    /// </summary>
    public virtual ValueTask<Result<Unit>> SendAsync(CanFrame frame, CancellationToken ct = default)
    {
        // M3 fix: use the ActiveChannel property (Volatile.Read) instead
        // of the backing field directly. Without this, the JIT may cache
        // _activeChannel in a register, causing SendAsync to see a stale
        // non-null value after ActiveChannel is set to null from another
        // thread (e.g. during DisconnectAsync).
        var ch = ActiveChannel;
        return ch is null
            ? ValueTask.FromResult(Result<Unit>.Fail(ErrorCode.InvalidState, "No active channel"))
            : ch.WriteAsync(frame, ct);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "SendService active channel changed to handle 0x{Handle:X2}")]
    private static partial void LogActiveChannelChanged(ILogger logger, ushort handle);

    [LoggerMessage(Level = LogLevel.Information, Message = "SendService active channel cleared (no channel connected)")]
    private static partial void LogActiveChannelCleared(ILogger logger);
}
