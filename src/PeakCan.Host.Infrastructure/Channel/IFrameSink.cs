using PeakCan.Host.Core;

namespace PeakCan.Host.Infrastructure.Channel;

/// <summary>
/// Receives frames from one or more <see cref="IFrameSource"/>s. Implementations
/// must be thread-safe — the <see cref="ChannelRouter"/> invokes
/// <see cref="OnFrame"/> on the SDK read thread without marshaling.
/// </summary>
public interface IFrameSink
{
    /// <summary>
    /// Called for every received frame on the SDK read thread.
    /// <para>
    /// <b>Contract:</b> implementations MUST NOT block. Heavy work
    /// (disk I/O, dictionary lookups, signal decoding) MUST be enqueued
    /// to an internal <see cref="System.Threading.Channels.Channel{T}"/>
    /// or off-thread worker. A blocking OnFrame stalls the SDK read loop
    /// and drops frames at high bus load.
    /// </para>
    /// <para>
    /// Implementations MUST NOT throw — exceptions are caught by the
    /// <c>ChannelRouter</c> and forwarded to <see cref="OnError"/> on
    /// the same sink.
    /// </para>
    /// </summary>
    void OnFrame(CanFrame frame);

    /// <summary>
    /// Called when another sink in the same router throws while handling
    /// the same frame. Implementations should log; they are not expected to
    /// stop the pipeline.
    /// </summary>
    void OnError(Exception ex);
}
