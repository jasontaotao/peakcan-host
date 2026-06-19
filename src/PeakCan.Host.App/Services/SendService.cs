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
/// <b>Thread-safety on <see cref="ActiveChannel"/>:</b> the setter is a
/// plain field write and is NOT synchronized. In the MVP it is invoked
/// on the UI thread by <c>AppShellViewModel.ConnectAsync</c> and read
/// on the UI thread by the send command; the race is benign because
/// both sides run on the dispatcher. If a future background thread
/// needs to write this property, the host must marshal to the UI
/// thread first (e.g. via <c>Dispatcher.Invoke</c>).
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
    /// instead of throwing.
    /// </summary>
    public ICanChannel? ActiveChannel
    {
        get => _activeChannel;
        set
        {
            if (ReferenceEquals(_activeChannel, value))
            {
                return;
            }
            _activeChannel = value;
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
        => _activeChannel is null
            ? ValueTask.FromResult(Result<Unit>.Fail(ErrorCode.InvalidState, "No active channel"))
            : _activeChannel.WriteAsync(frame, ct);

    [LoggerMessage(Level = LogLevel.Information, Message = "SendService active channel changed to handle 0x{Handle:X2}")]
    private static partial void LogActiveChannelChanged(ILogger logger, ushort handle);

    [LoggerMessage(Level = LogLevel.Information, Message = "SendService active channel cleared (no channel connected)")]
    private static partial void LogActiveChannelCleared(ILogger logger);
}
