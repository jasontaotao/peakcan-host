namespace PeakCan.HIL.Core.Uds;

/// <summary>
/// Phase 2: ISO 14229-1 §10.5 CommunicationControl (0x28) sub-functions.
/// Used for functional-address broadcast to enable/disable ECU communication.
/// Typical flash workflow: DisableRxAndTx before flashing, EnableRxAndTx after.
/// </summary>
public enum CommunicationSubFunction : byte
{
    /// <summary>Enable both Rx and Tx (normal mode, restore after flash).</summary>
    EnableRxAndTx = 0x00,

    /// <summary>Enable Rx but disable Tx.</summary>
    EnableRxDisableTx = 0x01,

    /// <summary>Disable both Rx and Tx (quiet all ECUs before flash).</summary>
    DisableRxAndTx = 0x02,

    /// <summary>Disable Rx but enable Tx.</summary>
    DisableRxEnableTx = 0x03,
}
