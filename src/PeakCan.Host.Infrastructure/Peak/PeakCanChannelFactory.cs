using Microsoft.Extensions.Logging;
using PeakCan.Host.Core;
using PeakCan.Host.Infrastructure.Channel;

namespace PeakCan.Host.Infrastructure.Peak;

/// <summary>
/// PEAK-backed <see cref="IChannelFactory"/>. Production DI binds this
/// to <see cref="IChannelFactory"/>; tests inject a fake instead.
/// <para>
/// The factory takes an <see cref="ILogger{T}"/> so the channel it
/// produces can log read-loop failures. Without the logger, those
/// failures would be silently swallowed (see <see cref="PeakCanChannel"/>
/// class doc).
/// </para>
/// </summary>
public sealed class PeakCanChannelFactory : IChannelFactory
{
    private readonly ILogger<PeakCanChannel> _logger;

    public PeakCanChannelFactory(ILogger<PeakCanChannel> logger)
    {
        _logger = logger;
    }

    public ICanChannel Create(ChannelId id) => new PeakCanChannel(id, _logger);
}
