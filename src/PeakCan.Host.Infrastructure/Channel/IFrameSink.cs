using PeakCan.Host.Core;

namespace PeakCan.Host.Infrastructure.Channel;

/// <summary>
/// Receives frames from one or more <see cref="IFrameSource"/>s. Implementations
/// must be thread-safe — the <see cref="ChannelRouter"/> invokes
/// <see cref="OnFrame"/> on the SDK read thread without marshaling.
/// </summary>
public interface IFrameSink
{
    /// <summary>Called for every received frame. Implementations must not throw.</summary>
    void OnFrame(CanFrame frame);

    /// <summary>
    /// Called when another sink in the same router throws while handling
    /// the same frame. Implementations should log; they are not expected to
    /// stop the pipeline.
    /// </summary>
    void OnError(Exception ex);
}
