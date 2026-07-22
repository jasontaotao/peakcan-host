namespace PeakCan.Host.Core.Uds.FlashPipeline;

/// <summary>
/// Maps 1:1 onto the UDS ECUReset (0x11) sub-function bytes per ISO 14229-1 §11.3.
/// Used by a flashing-pipeline step of kind <see cref="FlashStepKind.EcuReset"/>; the
/// PipelineExecutor casts to <c>byte</c> and passes it to
/// <see cref="global::PeakCan.Host.Core.Uds.UdsClient.EcuResetAsync"/>.
/// </summary>
public enum EcuResetType
{
    /// <summary>Hard Reset (0x01) — ECU fully restarts, boots new image. Default post-flash.</summary>
    HardReset = 0x01,

    /// <summary>Key Off/On Reset (0x02) — power-cycle reset.</summary>
    KeyOffOn = 0x02,

    /// <summary>Soft Reset (0x03) — warm reset, session state preserved.</summary>
    SoftReset = 0x03,
}
