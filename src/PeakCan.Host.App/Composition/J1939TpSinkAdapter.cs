using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.J1939;
using PeakCan.Host.Infrastructure.Channel;

namespace PeakCan.Host.App.Composition;

/// <summary>
/// Bridges the Core-layer <see cref="J1939TpLayer"/> onto the
/// Infrastructure-layer router fan-out by adapting it to
/// <see cref="IFrameSink"/> (structure mirrors
/// <see cref="IsoTpSinkAdapter"/>). This is the receive wiring for the
/// J1939 transport-protocol stack: without this adapter being attached to
/// the <see cref="ChannelRouter"/>, <see cref="J1939TpLayer.ProcessFrame"/>
/// has no production call site and the layer never sees incoming
/// TP.CM/TP.DT frames (PGN 0x00EC00 / 0x00EB00).
/// <para>
/// Layering: lives in the App layer (which already references both Core and
/// Infrastructure) so the Core layer stays free of any
/// <see cref="IFrameSink"/> dependency. The Core layer must not reach down
/// into the Infrastructure channel contract.
/// </para>
/// <para>
/// <b>Contract obligations</b> (from <see cref="IFrameSink"/>):
/// <list type="bullet">
///   <item><see cref="OnFrame"/> MUST NOT throw. The router runs it on the
///   SDK read thread; a throw is forwarded to <see cref="OnError"/> and a
///   sink whose <see cref="OnError"/> also throws is auto-detached.</item>
///   <item><see cref="OnFrame"/> MUST NOT block.</item>
/// </list>
/// The layer itself filters non-TP PGNs (ProcessFrame silently ignores
/// non-extended frames and PGNs other than 0x00EC00/0x00EB00), so this
/// adapter is a transparent pass-through and does NOT duplicate the filter.
/// But <c>TpCmMessage.Decode</c>/<c>TpDtMessage.Decode</c> (called at the
/// top of <see cref="J1939TpLayer.ProcessFrame"/> for TP PGNs) throws
/// <see cref="ArgumentException"/> on malformed TP data (short payload,
/// unknown control byte). Without containment here a single bad frame
/// would make this adapter a throw-on-every-frame sink that the router
/// auto-detaches after a couple of frames — silently severing the J1939
/// receive path. Hence <see cref="OnFrame"/> narrow-catches
/// <see cref="ArgumentException"/>: it satisfies the no-throw contract
/// without masking genuinely unexpected exceptions (OOM, etc.) that SHOULD
/// still surface via <see cref="OnError"/>.
/// </para>
/// </summary>
internal sealed class J1939TpSinkAdapter : IFrameSink
{
    private readonly J1939TpLayer _layer;
    private readonly ILogger<J1939TpSinkAdapter> _logger;

    /// <summary>
    /// Construct the adapter. <paramref name="logger"/> is optional to
    /// mirror the null-logger tolerance pattern used by
    /// <see cref="IsoTpSinkAdapter"/> / <see cref="ChannelRouter"/>
    /// (test fixtures / back-compat callers); production DI always
    /// supplies one.
    /// </summary>
    public J1939TpSinkAdapter(J1939TpLayer layer, ILogger<J1939TpSinkAdapter>? logger = null)
    {
        _layer = layer ?? throw new ArgumentNullException(nameof(layer));
        _logger = logger ?? NullLogger<J1939TpSinkAdapter>.Instance;
    }

    /// <summary>
    /// Forward every incoming frame to
    /// <see cref="J1939TpLayer.ProcessFrame"/>. The layer itself filters by
    /// PGN (0x00EC00 / 0x00EB00, extended frames only), so this method is a
    /// transparent pass-through and does NOT duplicate the CAN-ID/PGN
    /// filter — keeping a single source of truth avoids drift between the
    /// adapter and the layer.
    /// <para>
    /// The TP codecs' <see cref="ArgumentException"/> on malformed frames is
    /// narrow-caught here so a single bad frame does not turn this sink
    /// into a repeatedly-erroring one the router would auto-detach. Other
    /// exceptions are intentionally left to propagate to the router's
    /// per-sink isolation (→ <see cref="OnError"/>) so they remain
    /// observable.
    /// </para>
    /// </summary>
    public void OnFrame(CanFrame frame)
    {
        try
        {
            _layer.ProcessFrame(frame);
        }
        catch (ArgumentException ex)
        {
            // IFrameSink.OnFrame MUST NOT throw — narrow-catch the TP
            // codecs' malformed-frame exceptions so the SDK read thread
            // stays alive.
            _logger.LogDebug(ex, "J1939TpSinkAdapter dropped a malformed TP frame (CAN ID 0x{CanId:X}).", frame.Id.Raw);
        }
    }

    /// <summary>
    /// Called by the router when ANOTHER sink in the same fan-out throws
    /// while handling the same frame. This adapter is not the source of
    /// that failure, so it only logs; it must not throw (the router
    /// auto-detaches sinks whose OnError throws) and must not touch the
    /// <see cref="J1939TpLayer"/> — the layer has no notion of sibling
    /// errors.
    /// </summary>
    public void OnError(Exception ex)
        => _logger.LogWarning(ex, "J1939TpSinkAdapter: a sibling sink threw in the router fan-out; the J1939 receive path is unaffected.");
}
