using PeakCan.Host.Core.Uds;

namespace PeakCan.Host.Core.Tests.Uds;

/// <summary>
/// Test double for <see cref="IKeyDerivationAlgorithm"/>. Echoes the seed
/// XORed with the security level so tests can verify both seed and level
/// reached the algorithm.
/// </summary>
public class FakeKeyDerivationAlgorithm : IKeyDerivationAlgorithm
{
    public int CallCount { get; private set; }
    public byte[]? LastSeed { get; private set; }
    public byte? LastSecurityLevel { get; private set; }

    public byte[] ComputeKey(byte[] seed, byte securityLevel)
    {
        CallCount++;
        LastSeed = seed;
        LastSecurityLevel = securityLevel;
        var result = new byte[seed.Length];
        for (var i = 0; i < seed.Length; i++)
            result[i] = (byte)(seed[i] ^ securityLevel);
        return result;
    }
}
