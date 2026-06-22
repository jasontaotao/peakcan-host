using Microsoft.Extensions.Logging;
using PeakCan.Host.Core;
using PeakCan.Host.Infrastructure.Channel;

namespace PeakCan.Host.Infrastructure.Peak;

/// <summary>
/// PEAK-backed <see cref="IChannelFactory"/>. Production DI binds this
/// to <see cref="IChannelFactory"/>; tests inject a fake instead.
/// <para>
/// The factory takes an <see cref="ILogger{T}"/> and an
/// <see cref="IPcanReader"/> so the channel it produces can log
/// read-loop failures and be tested without real hardware.
/// </para>
/// </summary>
public sealed class PeakCanChannelFactory : IChannelFactory
{
    private readonly ILogger<PeakCanChannel> _logger;
    private readonly IPcanReader _reader;

    public PeakCanChannelFactory(ILogger<PeakCanChannel> logger, IPcanReader reader)
    {
        _logger = logger;
        _reader = reader;
    }

    public ICanChannel Create(ChannelId id) => new PeakCanChannel(id, _logger, _reader);
}
