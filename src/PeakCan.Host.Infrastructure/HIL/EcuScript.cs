using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Core.Uds.IsoTp;

namespace PeakCan.Host.Infrastructure.HIL;

/// <summary>
/// Parsed ECU simulator script. CanIdConfig is already in ECU perspective (IDs swapped from HIL perspective).
/// </summary>
public sealed record EcuScript(
    string Name,
    CanIdConfig CanIds,
    IReadOnlyList<UdsResponseRule> Rules);
