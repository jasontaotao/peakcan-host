using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Replay;
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
    /// v1.4.2 PATCH Item 3: on a failed <c>Result&lt;Unit&gt;</c> (no
    /// active channel, PEAK error), throw <see cref="ReplaySendException"/>
    /// so the caller (<see cref="ReplayService"/> via
    /// <see cref="ReplayTimeline"/>) can surface the first-failure to the
    /// UI via <c>PlaybackEndedEventArgs.Error</c>. Previously the result
    /// was silently dropped (user-hostile on no-channel: 10000 frames of
    /// silent drop = no feedback).
    /// </summary>
    public async ValueTask SendFrameAsync(ReplayFrame frame, CancellationToken ct = default)
    {
        // v3.18.4 → 重构：扩展格式判断已收敛到 parser 输出层（ReplayFrame.IsExtended）。
        // parser 掩码掉 BLF bit31 后填入裸 29 位 Id + IsExtended 标记，consumer 直接读，
        // 不再各自掩码。原 bit31 掩码逻辑见 BlfParser/CanMessageFlow.cs。
        var format = frame.IsExtended ? FrameFormat.Extended : FrameFormat.Standard;
        var canFrame = new CanFrame(
            Id: new CanId(frame.Id, format),
            Data: frame.Data,
            Flags: frame.Flags,
            Channel: ChannelId.None,
            Timestamp: default);
        var result = await _send.SendAsync(canFrame, ct).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            // v3.18.5 PATCH (BLF offline playback): InvalidState here means
            // "No active channel" / "Not connected" — the user is replaying
            // OFFLINE to view the timeline without hardware attached. That is
            // a legitimate use case (user confirmed "connected + offline both
            // must work"), not a failure. Pre-v3.18.5 the adapter threw here
            // on the FIRST frame → the timeline's first-failure handler
            // raised "Replay aborted" → offline playback was impossible.
            // Now: InvalidState is a silent skip (frame not sent, timeline
            // keeps advancing). Genuine hardware errors
            // (HardwareNotAvailable / IoError / Refused / etc.) still throw
            // — those are real failures the user must see, and they abort
            // playback so a dropped USB stick isn't a 10000-frame silent drop.
            if (result.Error?.Code == ErrorCode.InvalidState)
            {
                return;
            }
            throw new ReplaySendException(
                $"Replay frame send failed at t={frame.Timestamp}s, id=0x{frame.Id:X}: " +
                $"{result.Error?.Message ?? "unknown error"}");
        }
    }
}
