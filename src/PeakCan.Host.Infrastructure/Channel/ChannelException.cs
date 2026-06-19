namespace PeakCan.Host.Infrastructure.Channel;

/// <summary>
/// Thrown by <see cref="IFrameSink"/> implementations when an unrecoverable
/// error occurs while handling a frame. The <see cref="ChannelRouter"/>
/// forwards these via <see cref="IFrameSink.OnError"/> so the channel read
/// loop can keep going.
/// </summary>
public sealed class ChannelException : Exception
{
    public ChannelException(string msg, Exception? inner = null) : base(msg, inner) { }
}
