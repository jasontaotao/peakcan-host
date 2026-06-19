namespace PeakCan.Host.Infrastructure.Channel;

/// <summary>
/// A source of CAN frames that fans them out to attached <see cref="IFrameSink"/>s.
/// <para>
/// Reserved for the future <c>ChannelRouter</c> (multi-source fan-out). The
/// primary single-source channel (<see cref="ICanChannel"/>) does not
/// implement this interface — subscribers consume its <c>FrameReceived</c>
/// event directly. The interface is kept here so the router can be added in
/// a later task without an API break.
/// </para>
/// </summary>
public interface IFrameSource
{
    void AttachSink(IFrameSink sink);
    void DetachSink(IFrameSink sink);
}
