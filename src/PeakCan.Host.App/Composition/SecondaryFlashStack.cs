using Microsoft.Extensions.Logging;
using PeakCan.Host.App.ViewModels.Uds.FlashPipeline;
using PeakCan.Host.Core.Uds;
using PeakCan.Host.Core.Uds.FlashPipeline;
using PeakCan.Host.Core.Uds.IsoTp;
using PeakCan.Host.Core.Uds.KeyDerivation;
using PeakCan.Host.Infrastructure.Channel;

namespace PeakCan.Host.App.Composition;

/// <summary>
/// Production <see cref="ISecondaryFlashStack"/>: a per-flash secondary UDS execution stack
/// built from fresh, non-singleton components so it can run a programming session on a
/// CAN-ID pair distinct from the diagnostic one without disturbing the diagnostic
/// <see cref="UdsClient"/> singleton. Built once per Start by
/// <see cref="SecondaryFlashStackFactory"/> and owned by
/// <see cref="FlashPanelViewModel"/> for the run's lifetime.
/// <para>
/// <b>Component graph</b> (all fresh, NOT pulled from DI beyond the shared send/router/timer):
/// <list type="bullet">
/// <item>A secondary <see cref="IsoTpLayer"/> constructed with
/// <c>profile.ProgrammingCanId</c> and an async <c>sendFrame</c> delegate that forwards to
/// the SAME <see cref="CoreSendService"/> singleton the diagnostic IsoTpLayer uses. Sharing
/// the sender is correct: the sender is transport-aware but address-agnostic, and ISO-TP
///隔离靠 <see cref="IsoTpLayer"/> 的 ReceiveFlow 按 ResponseId 过滤 (ReceiveFlow.cs).
/// </item>
/// <item>A key algorithm selected from the SecurityAccess step:
/// <see cref="SecurityAccessMode.Dll"/> → a fresh <see cref="DllKeyDerivationAlgorithm"/>
/// (native handle, owned + disposed here); <see cref="SecurityAccessMode.Manual"/> → the
/// stateless <see cref="PlaceholderKeyAlgorithm"/> (no native handle; Manual's SendKey
/// payload is hex-decoded by PipelineExecutor and passed via the 3-arg overload, which
/// does not consult the injected algorithm — see SecurityFlow.cs).
/// </item>
/// <item>A secondary <see cref="UdsClient"/> (4-arg ctor: isoTp + keyAlgo + timer + logger)
/// — fresh per run so its read-only <c>_keyAlgorithm</c> binds to the run-specific algo,
/// not the diagnostic singleton's Placeholder (per the C3 ctor-capture decision).
/// </item>
/// <item>an <see cref="IsoTpSinkAdapter"/> bridging the secondary IsoTpLayer onto the shared
/// <see cref="ChannelRouter"/> fan-out for receive; attached/detached by this stack's own
/// lifecycle methods.
/// </item>
/// </list>
/// </para>
/// <para>
/// <b>Dispose order (CRITICAL — Risk Matrix #4):</b>
/// <see cref="DetachFromRouter"/> MUST run before <see cref="Dispose"/>, so the router stops
/// delivering frames to the IsoTpSinkAdapter BEFORE the IsoTpLayer is disposed. Then
/// Dispose tears down in the reverse-of-construction order:
/// <c>UdsClient.Dispose</c> (unsubscribes MessageReceived — TransportFlow.cs:57) →
/// <c>IsoTpLayer.Dispose</c> (releases the send semaphore — LifecycleFlow.cs:70) →
/// <c>DllKey.Dispose</c> (frees the native handle — only for Dll mode; Placeholder is a
/// no-op). Disposing the IsoTpLayer before the UdsClient would let the client's transport
/// flow touch a freed semaphore; disposing the DllKey first while a SendKey is still in
/// flight would invoke a freed native export. The order below enforces the safe sequence.
/// </para>
/// </summary>
internal sealed class SecondaryFlashStack : ISecondaryFlashStack
{
    private readonly ChannelRouter _router;
    private readonly IsoTpSinkAdapter _sink;
    private readonly UdsClient _client;
    private readonly IsoTpLayer _isoTp;
    private readonly DllKeyDerivationAlgorithm? _dllKey;
    private readonly ILogger<SecondaryFlashStack> _logger;
    private bool _disposed;
    private bool _attached;

    /// <summary>
    /// Internal ctor: dependencies are supplied by <see cref="SecondaryFlashStackFactory"/>
    /// from the DI container. Test code reaches this via InternalsVisibleTo (App.Tests).
    /// </summary>
    /// <param name="router">The shared channel router (fan-out for receive).</param>
    /// <param name="isoTp">The freshly-constructed secondary IsoTpLayer (programming CanId).</param>
    /// <param name="sink">The adapter wrapping <paramref name="isoTp"/> for the router.</param>
    /// <param name="client">The freshly-constructed secondary UdsClient.</param>
    /// <param name="dllKey">The freshly-constructed OEM key algorithm (Dll mode), or null for Manual mode.</param>
    /// <param name="logger">Diagnostics logger.</param>
    internal SecondaryFlashStack(
        ChannelRouter router,
        IsoTpLayer isoTp,
        IsoTpSinkAdapter sink,
        UdsClient client,
        DllKeyDerivationAlgorithm? dllKey,
        ILogger<SecondaryFlashStack> logger)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(isoTp);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(logger);

        _router = router;
        _isoTp = isoTp;
        _sink = sink;
        _client = client;
        _dllKey = dllKey;
        _logger = logger;
    }

    /// <summary>The secondary UdsClient the PipelineExecutor drives.</summary>
    public UdsClient Client => _client;

    /// <summary>
    /// Attach the receive adapter to the shared router. Idempotent — a second call is a
    /// no-op (guarded by <see cref="_attached"/>). ChannelRouter.AttachSink is itself
    /// idempotent (Sinks.partial.cs), so this guard is belt-and-suspenders rather than
    /// load-bearing, but it keeps the attach/detach pair balanced for the Dispose invariant.
    /// </summary>
    public void AttachToRouter()
    {
        if (_attached) return;
        _router.AttachSink(_sink);
        _attached = true;
    }

    /// <summary>
    /// Detach the receive adapter from the router. Idempotent. MUST run before
    /// <see cref="Dispose"/> so no late router frame is delivered to a disposing IsoTpLayer
    /// (half-down adapter routing to an unmapped IsoTp faults the SDK read thread).
    /// </summary>
    public void DetachFromRouter()
    {
        if (!_attached) return;
        _router.DetachSink(_sink);
        _attached = false;
    }

    /// <summary>
    /// Tear down the stack in reverse-of-construction order AFTER the router has been
    /// detached: client → isoTp → dllKey. Idempotent.
    /// <para>
    /// Each component's Dispose is isolated so a failure in one does not prevent the
    /// later (more native-bound) ones from running — a half-broken client.Dispose must
    /// still free the native DLL handle. Swallowed exceptions are logged at Warning so
    /// the leak is observable in the operator's log without turning teardown into a
    /// throw-back-into-finally hazard (the VM's finally calls this after a failed run).
    /// </para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Defensive: if the caller forgot DetachFromRouter, detach before disposal so the
        // contract holds even on an abnormal path. Idempotent — safe even if already detached.
        DetachFromRouter();

        try { _client.Dispose(); }
        catch (Exception ex) { _logger.LogWarning(ex, "SecondaryFlashStack: UdsClient.Dispose threw and was contained."); }

        try { _isoTp.Dispose(); }
        catch (Exception ex) { _logger.LogWarning(ex, "SecondaryFlashStack: IsoTpLayer.Dispose threw and was contained."); }

        try { _dllKey?.Dispose(); }
        catch (Exception ex) { _logger.LogWarning(ex, "SecondaryFlashStack: DllKey.Dispose threw and was contained."); }
    }
}

/// <summary>
/// Production <see cref="ISecondaryFlashStackFactory"/>: resolves the shared send/router/timer
/// singletons from DI once at construction, then builds a fresh <see cref="SecondaryFlashStack"/>
/// per <see cref="Build"/> call with a freshly-constructed IsoTpLayer + UdsClient + key algorithm
/// for the given profile's programming CAN-ID pair and SecurityAccess step.
/// <para>
/// Registered as a DI singleton in <c>AppHostBuilder</c> so the
/// <see cref="FlashPanelViewModel"/> receives the same factory across the app lifetime but a
/// FRESH stack per flash run (no shared mutable state between runs).
/// </para>
/// </summary>
internal sealed class SecondaryFlashStackFactory : ISecondaryFlashStackFactory
{
    private readonly CoreSendService _sendService;
    private readonly ChannelRouter _router;
    private readonly UdsTimer _timer;
    private readonly ILogger<IsoTpLayer> _isoTpLogger;
    private readonly ILogger<UdsSession> _sessionLogger;
    private readonly ILogger<SecondaryFlashStack> _stackLogger;

    /// <summary>Internal ctor — resolved by DI; tests reach it via InternalsVisibleTo.</summary>
    internal SecondaryFlashStackFactory(
        CoreSendService sendService,
        ChannelRouter router,
        UdsTimer timer,
        ILogger<IsoTpLayer> isoTpLogger,
        ILogger<UdsSession> sessionLogger,
        ILogger<SecondaryFlashStack> stackLogger)
    {
        ArgumentNullException.ThrowIfNull(sendService);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(timer);
        ArgumentNullException.ThrowIfNull(isoTpLogger);
        ArgumentNullException.ThrowIfNull(sessionLogger);
        ArgumentNullException.ThrowIfNull(stackLogger);

        _sendService = sendService;
        _router = router;
        _timer = timer;
        _isoTpLogger = isoTpLogger;
        _sessionLogger = sessionLogger;
        _stackLogger = stackLogger;
    }

    /// <inheritdoc/>
    public ISecondaryFlashStack Build(FlashStepSnapshot securityStep, FlashProfile profile)
    {
        ArgumentNullException.ThrowIfNull(securityStep);
        ArgumentNullException.ThrowIfNull(profile);

        // Secondary IsoTpLayer: programming CAN-ID pair from the profile. The sendFrame
        // delegate mirrors AppHostBuilder's diagnostic-IsoTp factory (line 200) verbatim:
        // forward to the SAME CoreSendService singleton (rate-limit exempt — ISO-TP owns
        // its STmin pacing; gating here breaks the transport state machine), ConfigureAwait(false)
        // to avoid STA capture on the WPF UI thread, and narrow-catch so a send failure is
        // logged exactly once via the layer's own LogIsoTpSendFailed.
        var isoTp = new IsoTpLayer(
            profile.ProgrammingCanId,
            async frame =>
            {
                try
                {
                    await _sendService.SendAsync(frame).ConfigureAwait(false);
                }
                catch (Exception ex) when (!(ex is IsoTpSendFailedException))
                {
                    IsoTpLayer.LogIsoTpSendFailed(_isoTpLogger, ex, frame.Id.Raw);
                }
            },
            _isoTpLogger);

        // Key algorithm: Dll mode loads the OEM native DLL (fresh per run, owned + disposed
        // by the stack). Manual mode uses the stateless Placeholder — the Manual SendKey
        // payload is hex-decoded by PipelineExecutor and passed via the 3-arg overload that
        // never consults the injected algorithm, so Placeholder is a safe inert stand-in.
        // Auto mode is refused by the VM BEFORE Build is called (Phase 1 placeholder); if a
        // snapshot with Auto ever reaches here it is a programming error — refuse defensively.
        DllKeyDerivationAlgorithm? dllKey = null;
        IKeyDerivationAlgorithm keyAlgo;
        if (securityStep.SecurityMode == SecurityAccessMode.Dll)
        {
            if (string.IsNullOrWhiteSpace(securityStep.DllPath))
            {
                // Dispose the IsoTpLayer we just built so no handle leaks before the throw.
                isoTp.Dispose();
                throw new InvalidOperationException(
                    "SecurityAccess Dll mode selected but no OEM DLL path was provided.");
            }
            dllKey = new DllKeyDerivationAlgorithm(securityStep.DllPath);
            keyAlgo = dllKey;
        }
        else if (securityStep.SecurityMode == SecurityAccessMode.Auto)
        {
            isoTp.Dispose();
            throw new NotImplementedException(
                "SecurityAccess Auto mode is not implemented in Phase 1. Select Manual or Dll.");
        }
        else
        {
            keyAlgo = new PlaceholderKeyAlgorithm();
        }

        // Secondary UdsClient (4-arg ctor from UdsClient.cs:94): binds its read-only
        // _keyAlgorithm to the run-specific algo, not the diagnostic singleton's Placeholder.
        var client = new UdsClient(isoTp, keyAlgo, _timer, _sessionLogger);
        var sink = new IsoTpSinkAdapter(isoTp);

        return new SecondaryFlashStack(_router, isoTp, sink, client, dllKey, _stackLogger);
    }
}
