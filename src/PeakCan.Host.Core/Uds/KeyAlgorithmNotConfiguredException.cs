namespace PeakCan.Host.Core.Uds;

/// <summary>
/// Thrown by <see cref="IKeyDerivationAlgorithm"/> implementations that
/// have not been configured with OEM-specific parameters. Distinct from
/// generic <see cref="InvalidOperationException"/> so the UI layer can
/// surface a targeted configuration hint instead of a generic error.
/// </summary>
public sealed class KeyAlgorithmNotConfiguredException : Exception
{
    public byte SecurityLevel { get; }

    public KeyAlgorithmNotConfiguredException(byte securityLevel)
        : base($"UDS SecurityAccess key algorithm for level 0x{securityLevel:X2} " +
               "is not configured. Register an IKeyDerivationAlgorithm implementation " +
               "in DI before calling SecurityAccessAsync.")
        => SecurityLevel = securityLevel;
}
