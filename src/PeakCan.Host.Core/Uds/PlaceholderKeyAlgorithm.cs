namespace PeakCan.Host.Core.Uds;

/// <summary>
/// Default <see cref="IKeyDerivationAlgorithm"/> implementation. Throws
/// <see cref="KeyAlgorithmNotConfiguredException"/> until an OEM-specific
/// implementation is registered in DI. Ships by default so the build,
/// tests, and app startup are all green without an OEM-supplied algorithm.
/// </summary>
public sealed class PlaceholderKeyAlgorithm : IKeyDerivationAlgorithm
{
    public byte[] ComputeKey(byte[] seed, byte securityLevel)
    {
        ArgumentNullException.ThrowIfNull(seed);
        throw new KeyAlgorithmNotConfiguredException(securityLevel);
    }
}
