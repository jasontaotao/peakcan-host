using PeakCan.Host.Core;
using PeakCan.Host.Infrastructure.Channel;

namespace PeakCan.Host.Infrastructure.Peak;

/// <summary>
/// PEAK-backed <see cref="IChannelFactory"/>. Production DI binds this
/// to <see cref="IChannelFactory"/>; tests inject a fake instead.
/// </summary>
public sealed class PeakCanChannelFactory : IChannelFactory
{
    public ICanChannel Create(ChannelId id) => new PeakCanChannel(id);
}
