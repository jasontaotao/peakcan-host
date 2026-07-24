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
    /// ISO 14229 预编程检查 (Pre-Programming Check). 刷写前执行：通过 0x31 RoutineControl
    /// 检查编程预条件（车速=0、供电电压范围、档位等）。RID 典型值 0xFF02。
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

    /// <summary>
    /// ISO 14229 编程依赖性检查 (Programming Dependency Check). 刷写完成后执行：通过 0x31
    /// RoutineControl 检查编程完整性 (CRC32) 和软硬件兼容性。RID 典型值 0xFF01。
    /// </summary>
    DependencyCheck,

    /// <summary>
    /// Phase 2: UDS CommunicationControl (0x28). Broadcast to all ECUs to enable/disable
    /// communication. Typical pre/post-flash step: DisableRxAndTx before flashing,
    /// EnableRxAndTx after. Uses functional addressing.
    /// </summary>
    CommunicationControl,

    /// <summary>
    /// Phase 2: UDS DTCControl (0x14). Clear or read DTCs. Typical post-flash step
    /// to clear DTCs triggered during flashing.
    /// </summary>
    DtcControl,
}
