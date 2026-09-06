using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Peak.Can.Basic.BackwardCompatibility;
using PeakCan.HIL.Core;
using PeakCan.Host.Infrastructure.Channel;
using PeakCan.Host.Core;

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
public sealed partial class PeakCanChannel : ChannelReadLoop, ICanChannel
{
    // Backoff schedule after consecutive read-loop failures. Resets to 0
    // whenever a read returns a non-error status (success or "queue empty").
    // P2-1 2026-09-06：调度/计数/give-up 收敛到 ChannelReadLoop 骨架（保留
    // const 别名维持测试兼容）；此处仅保留厂商 hook（见 ReadLoopFlow.cs）。
    internal new const int MaxConsecutiveReadFailures = ChannelReadLoop.MaxConsecutiveReadFailures;

    private readonly ushort _handle;
    private readonly ChannelConnectGate _gate = new();
    private readonly IPcanReader _reader;

    public bool IsConnected => _gate.IsConnected;
    public event Action<CanFrame>? FrameReceived;

    public PeakCanChannel(ChannelId id, ILogger<PeakCanChannel>? logger = null, IPcanReader? reader = null)
        : base(id, logger ?? NullLogger<PeakCanChannel>.Instance)
    {
        _handle = id.Handle;
        // NullLogger keeps test paths that new up the channel directly
        // (no DI) free of logger plumbing while still letting production
        // capture read-loop failures via the registered ILogger.
        // P2-1 2026-09-06：读循环日志走骨架自身的 logger；本类的 _logger 字段
        // 随之删除（原仅喂读循环日志）。
        // PcanReader is the production default; tests inject a fake.
        _reader = reader ?? new PcanReader();
    }

    // === Flow B methods moved to PeakCanChannel/NativeBindings.cs (W18 Task 2) ===
}
