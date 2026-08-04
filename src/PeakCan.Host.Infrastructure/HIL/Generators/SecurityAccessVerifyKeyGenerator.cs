using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.Host.Infrastructure.HIL.Generators;

/// <summary>
/// Verifies the key for UDS 0x27 subFunc even.
/// Key algorithm: expectedKey[i] = seed[i] ^ 0xAA.
/// Returns NRC 0x35 (invalidKey) on mismatch, positive response on success.
/// </summary>
public sealed class SecurityAccessVerifyKeyGenerator : IEcuResponseGenerator
{
    public string Name => "SecurityAccessVerifyKey";

    public byte[] Generate(byte[] request, string currentState, IEcuContext context)
    {
        if (!context.HasKey("SecuritySeed"))
            return new byte[] { 0x7F, 0x27, 0x22 }; // NRC conditionsNotCorrect

        var seed = context.Get<byte[]>("SecuritySeed")!;
        var expectedKey = seed.Select(b => (byte)(b ^ 0xAA)).ToArray();

        if (request.Length < 2 + expectedKey.Length)
            return new byte[] { 0x7F, 0x27, 0x13 }; // NRC incorrectMessageLength

        var receivedKey = request.Skip(2).Take(expectedKey.Length).ToArray();
        if (!receivedKey.SequenceEqual(expectedKey))
            return new byte[] { 0x7F, 0x27, 0x35 }; // NRC invalidKey

        context.Set("SecurityUnlocked", true);
        return new byte[] { 0x67, 0x02 }; // positive response
    }
}
