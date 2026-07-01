namespace PeakCan.Host.Core.Uds;

/// <summary>
/// v1.3.0 MINOR Item 2/4: ISO 14229-1 §10.2 ECUReset (0x11) sub-functions.
/// Standard values only; OEM-specific range (0x40-0x7F) is reachable via
/// the <c>byte</c> overload of <c>EcuResetAsync</c>.
/// </summary>
public enum UdsResetType : byte
{
    /// <summary>Hard reset — equivalent to power-on reset.</summary>
    HardReset = 0x01,

    /// <summary>Key-off-on reset — emulates ignition cycle.</summary>
    KeyOffOnReset = 0x02,

    /// <summary>Soft reset — ECU internal reset, no power cycle.</summary>
    SoftReset = 0x03,
}