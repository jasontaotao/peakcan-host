using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Uds.IsoTp;
using PeakCan.Host.Infrastructure.Channel;

namespace PeakCan.Host.App.Composition;

/// <summary>
/// Bridges the Core-layer <see cref="IsoTpLayer"/> onto the
/// Infrastructure-layer router fan-out by adapting it to
/// <see cref="IFrameSink"/>. This is the missing receive wiring: the
/// production UDS stack can send requests but, without this adapter being
/// attached to the <see cref="ChannelRouter"/>, never receives ECU
/// responses — <see cref="IsoTpLayer.ProcessFrame"/> has no production call
/// site and no <c>IFrameSink</c> implementation wires it to incoming frames.
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
/// The layer's own <see cref="IsoTpLayer.ProcessFrame"/> is non-blocking and
/// isolates its <see cref="IsoTpLayer.MessageReceived"/> subscribers in an
/// internal try/catch (so downstream subscriber throws do not escape). But
/// the FIRST thing <see cref="IsoTpLayer.ProcessFrame"/> does is
/// <see cref="IsoTpFrame.Decode"/>, which throws <see cref="ArgumentException"/>
/// on 6 classes of malformed input (empty data, unknown PCI, SF length 0, FF
/// too short, FF length < 8, FC too short). Without containment here a
/// single bad frame would make this adapter a throw-on-every-frame sink that
/// the router auto-detaches after a couple of frames — silently severing the
/// UDS receive path. Hence <see cref="OnFrame"/> narrow-catches
/// <see cref="ArgumentException"/>: it satisfies the no-throw contract
/// without masking genuinely unexpected exceptions (OOM, etc.) that SHOULD
/// still surface via <see cref="OnError"/>.
/// </para>
/// </summary>
internal sealed class IsoTpSinkAdapter : IFrameSink
{
    private readonly IsoTpLayer _isoTp;
    private readonly ILogger<IsoTpSinkAdapter> _logger;

    /// <summary>
    /// Construct the adapter. <paramref name="logger"/> is optional to
    /// mirror the null-logger tolerance pattern used by
    /// <see cref="ChannelRouter"/> (test fixtures / back-compat callers);
    /// production DI always supplies one.
    /// </summary>
    public IsoTpSinkAdapter(IsoTpLayer isoTp, ILogger<IsoTpSinkAdapter>? logger = null)
    {
        _isoTp = isoTp ?? throw new ArgumentNullException(nameof(isoTp));
        _logger = logger ?? NullLogger<IsoTpSinkAdapter>.Instance;
    }

    /// <summary>
    /// Forward every incoming frame to <see cref="IsoTpLayer.ProcessFrame"/>.
    /// The layer itself filters by its configured
    /// <c>CanIdConfig.ResponseId</c>, so this method is a transparent
    /// pass-through and does NOT duplicate the CAN-ID filter — keeping a
    /// single source of truth avoids drift between the adapter and the
    /// layer as the configured response ID changes (e.g. flashing uses a
    /// programming response ID distinct from the diagnostic one).
    /// <para>
    /// <see cref="IsoTpFrame.Decode"/>'s <see cref="ArgumentException"/> on
    /// malformed frames is narrow-caught here so a single bad frame does not
    /// turn this sink into a repeatedly-erroring one the router would
    /// auto-detach. Other exceptions are intentionally left to propagate to
    /// the router's per-sink isolation (→ <see cref="OnError"/>) so they
    /// remain observable.
    /// </para>
    /// </summary>
    public void OnFrame(CanFrame frame)
    {
        try
        {
            _isoTp.ProcessFrame(frame);
        }
        catch (ArgumentException ex)
        {
            // IFrameSink.OnFrame MUST NOT throw — narrow-catch Decode's
            // malformed-frame exceptions so the SDK read thread stays alive.
            _logger.LogDebug(
                ex,
                "IsoTpSinkAdapter dropped a malformed ISO-TP frame (CAN ID 0x{CanId:X}).",
                frame.Id.Raw);
        }
    }

    /// <summary>
    /// Called by the router when ANOTHER sink in the same fan-out throws
    /// while handling the same frame. This adapter is not the source of
    /// that failure, so it only logs; it must not throw (the router
    /// auto-detaches sinks whose OnError throws) and must not touch the
    /// <see cref="IsoTpLayer"/> — the layer has no notion of sibling errors.
    /// </summary>
    public void OnError(Exception ex)
        => _logger.LogWarning(
            ex,
            "IsoTpSinkAdapter: a sibling sink threw in the router fan-out; the ISO-TP receive path is unaffected.");
}
