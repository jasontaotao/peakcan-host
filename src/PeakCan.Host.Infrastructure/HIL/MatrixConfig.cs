using PeakCan.HIL.Core.HIL;

namespace PeakCan.Host.Infrastructure.HIL;

/// <summary>
/// Parsed multi-ECU matrix configuration.
/// </summary>
public sealed record MatrixConfig(
    string Name,
    IReadOnlyList<EcuScript> Ecus);
