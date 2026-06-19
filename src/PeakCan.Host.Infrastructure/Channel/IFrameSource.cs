namespace PeakCan.Host.Infrastructure.Channel;

/// <summary>
/// A source of CAN frames that fans them out to attached <see cref="IFrameSink"/>s.
/// Implemented by <see cref="ICanChannel"/> (single-source) and
/// <see cref="ChannelRouter"/> (multi-source fan-out).
/// </summary>
public interface IFrameSource
{
    void AttachSink(IFrameSink sink);
    void DetachSink(IFrameSink sink);
}
