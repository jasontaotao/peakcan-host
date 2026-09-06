using PeakCan.Host.Core;

namespace PeakCan.Host.Core;

/// <summary>
/// Result of a hardware-probe call. The probe is a non-destructive
/// <c>Initialize</c> + <c>Uninitialize</c> sequence used to detect
/// whether a PEAK channel is physically present before a real Connect.
/// <para>
/// <b>Why a result type instead of a thrown exception?</b> the probe
/// is a best-effort check expected to fail when no hardware is
/// attached. Surfacing a structured result (Ok + Message) lets the
/// caller update UI status without exception-handling boilerplate.
/// </para>
/// </summary>
/// <param name="Ok">True if the channel responded; false otherwise.</param>
/// <param name="Message">Human-readable status, safe to bind to UI.</param>
public sealed record ProbeResult(bool Ok, string Message);

/// <summary>
/// MVP channel probe: asks the underlying SDK whether a handle is
/// reachable. The MVP only supports a single hardcoded handle; v1.1
/// will add multi-channel enumeration.
/// <para>
/// <b>Why an interface in Core?</b> the App layer (WPF VM) must not
/// reference the PEAK SDK directly — that's the architectural invariant
/// enforced by Task 18's NetArchTest rule
/// <c>App_Should_Not_Depend_On_Peak_Can_Basic</c>. By putting the
/// contract in Core and the implementation in Infrastructure.Peak, the
/// adapter can be swapped (e.g. for a SocketCAN backend in v1.1)
/// without touching the VM.
/// </para>
/// </summary>
public interface IChannelProbe
{
    /// <summary>
    /// Probe the channel at <paramref name="handle"/>. Non-throwing:
    /// any SDK failure (no driver, no hardware) is surfaced as
    /// <c>ProbeResult.Ok = false</c> with a diagnostic message.
    /// </summary>
    ProbeResult Probe(ushort handle);
}
