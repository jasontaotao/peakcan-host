using PeakCan.Host.Core.HIL.Contracts;

namespace PeakCan.Host.Infrastructure.HIL.Generators;

/// <summary>
/// Generates a 4-byte security seed (UDS 0x27 subFunc odd).
/// Caches the seed in context so repeated requests return the same value.
/// </summary>
public sealed class SecurityAccessSeedGenerator : IEcuResponseGenerator
{
    public string Name => "SecurityAccessSeed";

    public byte[] Generate(byte[] request, string currentState, IEcuContext context)
    {
        if (!context.HasKey("SecuritySeed"))
        {
            var seed = new byte[4];
            Random.Shared.NextBytes(seed);
            context.Set("SecuritySeed", seed);
        }

        var seedBytes = context.Get<byte[]>("SecuritySeed")!;
        return new byte[] { 0x67, 0x01 } // positive response SID|0x40 + subFunc
            .Concat(seedBytes).ToArray();
    }
}
