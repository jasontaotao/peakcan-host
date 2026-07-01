namespace PeakCan.Host.Core.Uds.Database;

/// <summary>
/// A single Diagnostic Trouble Code definition, mirrored from
/// ODX <c>DTC-DOP</c> via the ODX importer. Stored in
/// <see cref="DtcDatabase"/>.
/// </summary>
public readonly record struct DtcDefinition(
    /// <summary>3-byte DTC code (e.g., 0x123456).</summary>
    uint Code,

    /// <summary>Short human-readable name (e.g., "P0123").</summary>
    string ShortName,

    /// <summary>Long description (e.g., "O2 sensor circuit malfunction").</summary>
    string Description,

    /// <summary>Status mask bits per ISO 14229-1 D.2 (bit 0 = testFailed, etc.).</summary>
    byte StatusMask);
