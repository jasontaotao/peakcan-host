// ChannelRouter/Channels.partial.cs — W25 T3 (Flow A, smallest)
// Channel-list registration: RegisterChannel (idempotent add +
// FrameReceived subscription) + UnregisterChannel (idempotent remove
// + FrameReceived unsubscription). Both gated by the channel-router
// lock; OnChannelFrame (in FrameRouting.partial.cs) is the delegate
// target registered here.
//
// Sister of W18 PeakCanChannel/ReadLoopFlow.cs subscription-then-attach
// pattern: same lock-gated list mutator + event subscription shape.
//
// W25 T3 verbatim re-extracted via `git show HEAD:src/...cs | sed -n '112,134p'`
// per W20 T2 R1 fabrication LESSON (23rd application).

using System.Collections.Immutable;
using PeakCan.Host.Core;

namespace PeakCan.Host.Infrastructure.Channel;

public sealed partial class ChannelRouter
{
    public void RegisterChannel(ICanChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        lock (_gate)
        {
            if (_channels.Contains(channel)) return;
            _channels.Add(channel);
            channel.FrameReceived += OnChannelFrame;
        }
    }

    /// <summary>Unsubscribe from <paramref name="channel"/>. Idempotent.</summary>
    public void UnregisterChannel(ICanChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        lock (_gate)
        {
            if (_channels.Remove(channel))
            {
                channel.FrameReceived -= OnChannelFrame;
            }
        }
    }
}
