namespace PeakCan.HIL.Core.Uds;

/// <summary>
/// Phase 2: ISO 14229-1 §10.5 DTCControl (0x14) sub-functions.
/// Used by <c>UdsClient.DtcControlAsync</c> for the DtcControl flash-pipeline step.
/// </summary>
public enum DtcControlSubFunction : byte
{
    /// <summary>Report DTC by status mask.</summary>
    ReportDTCByStatusMask = 0x01,

    /// <summary>Report DTC snapshot identification.</summary>
    ReportDTCSnapshotIdentification = 0x02,

    /// <summary>Report DTC snapshot record by DTC number.</summary>
    ReportDTCSnapshotRecordByDTCNumber = 0x03,

    /// <summary>Report DTC extended data record by DTC number.</summary>
    ReportDTCExtendedDataRecordByDTCNumber = 0x06,

    /// <summary>Report supported DTC.</summary>
    ReportSupportedDTC = 0x0A,

    /// <summary>Report first test failed DTC.</summary>
    ReportFirstTestFailedDTC = 0x0B,

    /// <summary>Report first confirmed DTC.</summary>
    ReportFirstConfirmedDTC = 0x0C,

    /// <summary>Report most recent test failed DTC.</summary>
    ReportMostRecentTestFailedDTC = 0x0D,

    /// <summary>Report most recent confirmed DTC.</summary>
    ReportMostRecentConfirmedDTC = 0x0E,

    /// <summary>Report mirror memory DTC by status mask.</summary>
    ReportMirrorMemoryDTCByStatusMask = 0x10,

    /// <summary>Report emissions-related OBD DTC by status mask.</summary>
    ReportEmissionsRelatedOBDDTCByStatusMask = 0x12,

    /// <summary>Report DTC fault detection counter.</summary>
    ReportDTCFaultDetectionCounter = 0x14,

    /// <summary>Report DTC with permanent status.</summary>
    ReportDTCWithPermanentStatus = 0x15,

    /// <summary>Clear all DTCs (service 0x14 with this sub-function).</summary>
    ClearDTCInformation = 0x14,
}
