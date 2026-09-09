namespace PeakCan.Host.Core.Uds;

/// <summary>
/// Built-in XOR-0xAA seed-key algorithm (M3.4, spec 2026-09-07 Phase 3):
/// key[i] = seed[i] ^ 0xAA — matches the virtual ECU's built-in seed
/// generator (SecurityAccessServer default). Selectable via
/// <c>--key-algorithm builtin</c>; OEM DLLs remain the production path.
/// </summary>
public sealed class XorAAKeyDerivationAlgorithm : IKeyDerivationAlgorithm
{
    public byte[] ComputeKey(byte[] seed, byte securityLevel)
    {
        ArgumentNullException.ThrowIfNull(seed);
        return seed.Select(b => (byte)(b ^ 0xAA)).ToArray();
    }
}
