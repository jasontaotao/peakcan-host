using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Peak.Can.Basic.BackwardCompatibility;
using PeakCan.Host.Core;
using PeakCan.Host.Infrastructure.Channel;

namespace PeakCan.Host.Infrastructure.Peak;

/// <summary>
/// PEAK PCAN-Basic adapter implementing <see cref="ICanChannel"/>. Wraps the
/// static <c>Peak.Can.Basic.BackwardCompatibility.PCANBasic</c> API for one
/// <see cref="ushort"/> channel handle.
/// <para>
/// Read path: a single background <see cref="Task"/> polls
/// <c>PCANBasic.Read</c> / <c>PCANBasic.ReadFD</c> until cancelled, raising
/// <see cref="FrameReceived"/> on the SDK thread.
/// </para>
/// <para>
/// Write path: <see cref="WriteAsync"/> formats the <see cref="CanFrame"/>
/// into a <c>TPCANMsg</c> / <c>TPCANMsgFD</c> and calls the synchronous
/// <c>PCANBasic.Write*</c>. The <c>TPCANMessageType</c> bit pattern selects
/// standard-vs-extended and FD-vs-classical; raw 11/29-bit IDs go into
/// <c>ID</c> without any IDE-bit flag.
/// </para>
/// <para>
/// Errors are translated via <see cref="PeakErrorMapper"/>; unexpected
/// exceptions are caught at the boundary and reported as
/// <see cref="ErrorCode.IoError"/> / <see cref="ErrorCode.HardwareNotAvailable"/>
/// rather than propagated, so the WPF ViewModel layer can render a message
/// without try/catch boilerplate.
/// </para>
/// <para>
/// <b>Read loop fault handling:</b> any exception thrown from the SDK read
/// calls is caught, logged at error level, and the loop backs off
/// (1ms / 10ms / 50ms per consecutive failure) to prevent a hot loop.
/// If <see cref="MaxConsecutiveReadFailures"/> consecutive failures
/// accumulate, the read loop gives up rather than busy-spinning on a
/// dead bus; the channel remains connected from the SDK's perspective
/// but no frames will be delivered. The classic and FD read blocks each
/// have their own try/catch so a subscriber that throws on
/// <see cref="FrameReceived"/> for a classic frame cannot skip the FD
/// read in the same iteration.
/// </para>
/// <para>
/// <b>v3.16.9.4 PATCH — read-loop errors surface to UI:</b> in addition
/// to the <c>ILogger</c> calls above, every per-iteration failure also
/// raises <see cref="ICanChannel.ReadLoopError"/>, and the give-up event
/// (after <see cref="MaxConsecutiveReadFailures"/> failures) raises the
/// same event with <see cref="ReadLoopErrorKind.LoopGivingUp"/>. Production
/// UI (e.g. <c>AppShellViewModel</c>) subscribes to this event and updates
/// the StatusMessage so bus-off / driver-unload / hardware-fault conditions
/// are visible to the operator instead of looking like a "connected but
/// no frames" state. Pre-v3.16.9.4 the read loop only logged; see
/// <c>docs/release-notes-v3.16.9.4.md</c> for the full rationale.
/// </para>
/// <para>
/// <b>Classic baud dispatch:</b> <see cref="BaudRate"/> in Core
/// no longer carries the PEAK <c>TPCANBaudrate?</c> field (Core must not
/// depend on the PEAK SDK per NetArchTest rule 2). For classic CAN
/// (<c>fd: false</c>) this adapter maps the four preset
/// <see cref="BaudRate.Name"/> values back to the matching
/// <c>PCAN_BAUD_*</c> enum via <see cref="ResolveClassicCode"/>.
/// </para>
/// </summary>
public sealed partial class PeakCanChannel : ICanChannel
{
    // Backoff schedule after consecutive read-loop failures. Resets to 0
    // whenever a read returns a non-error status (success or "queue empty").
    private static readonly int[] ReadLoopBackoffMs = { 1, 10, 50 };

    /// <summary>
    /// After this many consecutive read-loop failures, the loop gives up
    /// rather than busy-spinning on a dead bus / unloaded driver. The
    /// channel stays in the connected state from the SDK's perspective
    /// (so a future manual disconnect still works), but no frames will
    /// be delivered until the user calls Disconnect + Connect again.
    /// </summary>
    internal const int MaxConsecutiveReadFailures = 100;

    private readonly ushort _handle;
    private readonly ChannelConnectGate _gate = new();
    private readonly ILogger<PeakCanChannel> _logger;
    private readonly IPcanReader _reader;

    public ChannelId Id { get; }
    public bool IsConnected => _gate.IsConnected;
    public event Action<CanFrame>? FrameReceived;
    /// <summary>
    /// v3.16.9.4 PATCH: surface read-loop failures to the UI layer. Raised
    /// on the SDK read thread (subscribers must marshal to UI). Fires
    /// <i>in addition to</i> the existing ILogger.LogError / LogCritical
    /// calls — the event is additive so production Serilog captures still
    /// include the full stack trace for post-mortem.
    /// </summary>
    public event Action<ReadLoopError>? ReadLoopError;

    public PeakCanChannel(ChannelId id, ILogger<PeakCanChannel>? logger = null, IPcanReader? reader = null)
    {
        Id = id;
        _handle = id.Handle;
        // NullLogger keeps test paths that new up the channel directly
        // (no DI) free of logger plumbing while still letting production
        // capture read-loop failures via the registered ILogger.
        _logger = (ILogger<PeakCanChannel>?)logger ?? NullLogger<PeakCanChannel>.Instance;
        // PcanReader is the production default; tests inject a fake.
        _reader = reader ?? new PcanReader();
    }





    static partial void LogReadLoopException(ILogger logger, ushort handle, string kind, Exception error);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Read loop giving up on handle 0x{Handle:X2} after {Failures} consecutive failures — bus appears dead, call Disconnect+Connect to recover")]
    private static partial void LogReadLoopGivingUp(ILogger logger, ushort handle, int failures);

    /// <summary>
    /// v3.16.9.4 PATCH: invoke <see cref="ReadLoopError"/> with a per-subscriber
    /// try/catch so a misbehaving subscriber (e.g. a UI handler that throws on
    /// a disposed Dispatcher) cannot crash the SDK read loop. Mirrors the
    /// sink-OnError isolation pattern in <c>ChannelRouter</c>: the loop is
    /// the high-priority thread, the subscriber is best-effort.
    /// </summary>
    static partial void LogReadLoopSubscriberThrew(ILogger logger, ushort handle, string subscriber, Exception ex);

    // === Flow B methods moved to PeakCanChannel/NativeBindings.cs (W18 Task 2) ===
}
