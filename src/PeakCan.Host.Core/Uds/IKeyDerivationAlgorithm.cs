namespace PeakCan.Host.Core.Uds;

/// <summary>
/// OEM-specific key derivation algorithm for UDS SecurityAccess (0x27).
/// Implementations are typically OEM-confidential and may call into
/// native libraries, network services, or hardware security modules.
/// </summary>
public interface IKeyDerivationAlgorithm
{
    /// <summary>
    /// Computes the response key for the given seed and security level.
    /// </summary>
    /// <param name="seed">Bytes returned by SecurityAccess requestSeed.</param>
    /// <param name="securityLevel">Sub-function byte (0x01, 0x03, ...).</param>
    /// <returns>Computed key bytes. Length is OEM-specific.</returns>
    /// <exception cref="ArgumentNullException">seed is null.</exception>
    /// <exception cref="KeyAlgorithmNotConfiguredException">
    ///   Thrown by placeholder implementations when no OEM algorithm is
    ///   registered. OEM implementations should throw other exceptions
    ///   (e.g. <see cref="InvalidOperationException"/>) on algorithm failure.
    /// </exception>
    byte[] ComputeKey(byte[] seed, byte securityLevel);
}
