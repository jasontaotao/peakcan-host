using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Core.Uds.IsoTp;

namespace PeakCan.Host.Infrastructure.HIL;

/// <summary>
/// Parsed ECU simulator script. CanIdConfig is in ECU perspective (IDs swapped from HIL perspective).
/// StateMachine encapsulates both stateless (Phase 3) and stateful (Phase 4) rules.
/// </summary>
public sealed record EcuScript(
    string Name,
    CanIdConfig CanIds,
    EcuStateMachine StateMachine,
    Dictionary<ushort, byte[]>? DidValues = null);
