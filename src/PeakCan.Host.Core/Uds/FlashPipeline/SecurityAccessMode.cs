namespace PeakCan.Host.Core.Uds.FlashPipeline;

/// <summary>
/// How a flashing-pipeline step of kind <see cref="FlashStepKind.SecurityAccess"/>
/// obtains the SendKey payload (the second leg of the 2-phase 0x27 exchange). Maps onto
/// the two <see cref="global::PeakCan.Host.Core.Uds.UdsClient"/> SecurityAccess overloads:
/// <list type="bullet">
/// <item><see cref="Manual"/> — operator-typed hex bytes; calls the 3-arg
/// <c>SecurityAccessAsync(level, key, ct)</c> and bypasses any injected
/// <c>IKeyDerivationAlgorithm</c>. Never blocks on DLL discovery.</item>
/// <item><see cref="Dll"/> — runtime-loaded <c>DllKeyDerivationAlgorithm</c> spawned in a
/// secondary UdsClient construction; calls the 2-arg
/// <c>SecurityAccessAsync(level, ct)</c> that runs seed→ComputeKey(DLL)→SendKey.</item>
/// <item><see cref="Auto"/> — Phase 1 placeholder: would reuse a DI-registered
/// <c>IKeyDerivationAlgorithm</c> singleton (e.g. a real OEM DLL registered at deploy time).
/// Not implemented in Phase 1 because it implies a deploy-time DI registration doc story;
/// UI renders it greyed and PipelineExecutor throws <c>NotImplementedException</c> if
/// selected. Tracked for Phase 3.</item>
/// </list>
/// </summary>
public enum SecurityAccessMode
{
    /// <summary>Operator-typed key hex — the never-blocked fallback.</summary>
    Manual,

    /// <summary>OEM native DLL key derivation — runtime-loaded.</summary>
    Dll,

    /// <summary>Phase 1 placeholder. Reuse DI-registered key algorithm. Not yet implemented.</summary>
    Auto,
}
