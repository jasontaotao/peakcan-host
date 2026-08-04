using PeakCan.HIL.Core;

namespace PeakCan.Host.Infrastructure.HIL;

/// <summary>
/// Subscribes to ICanChannel.FrameReceived in constructor, unsubscribes in Dispose.
/// Thread-safe unsubscribe via Interlocked.Exchange.
/// </summary>
internal sealed class FrameReceivedSubscription : IDisposable
{
    private ICanChannel? _channel;
    private readonly Action<CanFrame> _handler;

    public FrameReceivedSubscription(ICanChannel channel, Action<CanFrame> handler)
    {
        _channel = channel;
        _handler = handler;
        channel.FrameReceived += handler;
    }

    public void Dispose()
    {
        var ch = Interlocked.Exchange(ref _channel, null);
        if (ch is not null) ch.FrameReceived -= _handler;
    }
}
