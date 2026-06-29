using PeakCan.Host.Core;
using PeakCan.Host.Core.Replay;
using PeakCan.Host.App.Services;

namespace PeakCan.Host.App.Composition;

/// <summary>
/// v1.4.0 MINOR Replay: routes <see cref="IReplayService"/> emitted frames
/// to the live bus via <see cref="SendService"/>, the App-layer singleton
/// that owns the active <see cref="ICanChannel"/> and calls
/// <c>ICanChannel.WriteAsync</c> on its behalf.
/// <para>
/// DI singleton: registered as <see cref="IReplayFrameSink"/>.
/// </para>
/// <para>
/// <b>Mapping <see cref="ReplayFrame"/> → <see cref="CanFrame"/>:</b>
/// <c>ReplayFrame</c> is a parsed-ASC projection: <c>uint Id</c> + <c>byte Dlc</c>
/// + <c>byte[] Data</c>. <see cref="CanFrame"/> is the immutable Core record
/// that carries <c>CanId (raw + format)</c>, <c>ReadOnlyMemory&lt;byte&gt; Data</c>,
/// <c>FrameFlags</c>, <c>ChannelId</c>, <c>Timestamp</c>. We default the
/// channel to <c>ChannelId.None</c> and the timestamp to <c>default</c> —
/// <see cref="SendService.SendAsync"/> forwards the frame as-is to the
/// PEAK adapter which stamps the actual wire timestamp. ASC files do not
/// preserve the original hardware channel, so <c>None</c> is the
/// semantically correct value.
/// </para>
/// <para>
/// <b>Why <see cref="SendService"/>, not <see cref="PeakCan.Host.Infrastructure.Channel.ChannelRouter"/>:</b>
/// <see cref="PeakCan.Host.Infrastructure.Channel.ChannelRouter"/> is a
/// receiver-only fan-out (channels → sinks) — it does not write frames.
/// The single outbound path in this codebase is
/// <see cref="SendService.SendAsync"/>, which already knows how to return
/// <c>Result&lt;Unit&gt;.Fail(InvalidState)</c> when no channel is connected
/// and to forward to <c>ICanChannel.WriteAsync</c> when one is. Replay goes
/// through that same path so it shares the active-channel bookkeeping and
/// the Failed/OK result handling downstream code already understands.
/// </para>
/// </summary>
public sealed class ReplayFrameSinkAdapter : IReplayFrameSink
{
    private readonly SendService _send;

    /// <summary>Construct the adapter. <paramref name="send"/> must be the DI-singleton instance.</summary>
    public ReplayFrameSinkAdapter(SendService send)
    {
        _send = send ?? throw new ArgumentNullException(nameof(send));
    }

    /// <summary>
    /// Convert <paramref name="frame"/> into a <see cref="CanFrame"/> and
    /// forward to the active channel via <see cref="SendService.SendAsync"/>.
    /// Result is intentionally discarded: playback continues even if a
    /// single frame send fails (mirrors the <see cref="ReplayService"/>
    /// sink-throw tolerance added in Task 2 I-1).
    /// </summary>
    public async ValueTask SendFrameAsync(ReplayFrame frame, CancellationToken ct = default)
    {
        var canFrame = new CanFrame(
            Id: new CanId(frame.Id, FrameFormat.Standard),
            Data: frame.Data,
            Flags: frame.Flags,
            Channel: ChannelId.None,
            Timestamp: default);
        // SendAsync returns Result<Unit>; a failed result (no channel,
        // PEAK error) is silently dropped here. Playback continues with
        // the next frame; the underlying SendService logger records the
        // failure. This matches the design intent of IReplayFrameSink:
        // "the timer callback cannot await user error policy".
        await _send.SendAsync(canFrame, ct).ConfigureAwait(false);
    }
}
