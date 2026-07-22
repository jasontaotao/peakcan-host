namespace PeakCan.Host.Core.Uds.FlashPipeline;

/// <summary>
/// Enumerates the discrete kinds a flashing-pipeline step can be. The flashing
/// pipeline renders one row per <see cref="global::PeakCan.Host.App.ViewModels.Uds.FlashPipeline.FlashStep"/>
/// from a default template and the PipelineExecutor dispatches each enabled row onto the
/// matching UDS service. Kinds are intentionally a closed set — the pipeline remains a
/// configurable <i>step sequence</i>, not an extensible plugin list — so the UI column
/// layout and the executor switch can stay in lockstep.
/// </summary>
public enum FlashStepKind
{
    /// <summary>
    /// Phase 1 placeholder only. Precondition checks (vehicle speed, supply voltage,
    /// DTC/档位, OEM-specific DIDs/Routines) differ wildly across OEMs; the enum value
    /// exists so the UI can render a greyed-out "Coming in Phase N" row, but the App-layer
    /// FlashStep.IsEnabled defaults to false and PipelineExecutor skips any enabled
    /// PreCheck step until Phase 2 implements it. Phase 1 operators do pre-checks manually.
    /// </summary>
    PreCheck,

    /// <summary>UDS SessionControl (service 0x10), sub 0x03 = Programming.</summary>
    SessionControl,

    /// <summary>UDS SecurityAccess (service 0x27). Three modes — see <see cref="SecurityAccessMode"/>.</summary>
    SecurityAccess,

    /// <summary>UDS RoutineControl (0x31) EraseMemory. Routine 0xFF00 by default.</summary>
    Erase,

    /// <summary>UDS RequestDownload (0x34) + TransferData (0x36) loop + RequestTransferExit (0x37).</summary>
    DownloadTransfer,

    /// <summary>UDS RoutineControl (0x31) for OEM-defined verify (checksum/signature). Optional + OEM-gated.</summary>
    Verify,

    /// <summary>UDS ECUReset (0x11) — default Hard Reset to boot the new image.</summary>
    EcuReset,
}
