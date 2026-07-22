using PeakCan.Host.Core.Uds;
using PeakCan.Host.Core.Uds.FlashPipeline;

namespace PeakCan.Host.App.ViewModels.Uds.FlashPipeline;

/// <summary>
/// A flash-time secondary UDS execution stack: an independent IsoTpLayer (programming
/// CAN-ID pair) + a UdsClient injected with an OEM key algorithm (Dll or Placeholder),
/// wrapped in an IsoTpSinkAdapter for router fan-out receive. Owned by the
/// <see cref="FlashPanelViewModel"/> for the duration of one flash run and torn down in a
/// strict order (see <see cref="Dispose"/>) on Stop / completion / failure.
/// <para>
/// Publicly <see cref="Client"/> surfaces the live <see cref="UdsClient"/> the executor
/// drives; <see cref="AttachToRouter"/> / <see cref="DetachFromRouter"/> wire/disconnect
/// the receive adapter so lifecycle ownership stays inside the stack.
/// </para>
/// </summary>
internal interface ISecondaryFlashStack : IDisposable
{
    /// <summary>The secondary UdsClient (programming-session IsoTp + injected key algo).</summary>
    UdsClient Client { get; }

    /// <summary>Attach the ISO-TP receive adapter to the shared channel router.</summary>
    void AttachToRouter();

    /// <summary>
    /// Detach the receive adapter from the router. MUST run BEFORE Client.Dispose so no
    /// in-flight frame is delivered to a disposing IsoTpLayer — half-down adapter would
    /// route a late frame to an unmapped IsoTp and fault.
    /// </summary>
    void DetachFromRouter();
}
