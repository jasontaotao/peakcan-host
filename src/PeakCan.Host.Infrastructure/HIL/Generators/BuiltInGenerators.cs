using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.Host.Infrastructure.HIL.Generators;

/// <summary>
/// Phase 7 Unit B: single source of truth for the built-in ECU response generators.
/// Replaces <c>EcuScriptLoader.GetBuiltInGenerators()</c> so both EcuScriptLoader and
/// GeneratorPluginManager merge from the same list (spec §3.2, code-review L1/L2).
/// </summary>
public static class BuiltInGenerators
{
    public static IReadOnlyList<IEcuResponseGenerator> CreateAll() => new IEcuResponseGenerator[]
    {
        new SecurityAccessSeedGenerator(),
        new SecurityAccessVerifyKeyGenerator(),
        new ClearDtcGenerator(),
        new DidReadoutGenerator(),
        new DidWriteGenerator(),
    };
}
