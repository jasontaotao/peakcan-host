namespace PeakCan.Host.Core.Uds.Odx;

/// <summary>
/// Errors emitted during ODX / PDX import. Mirrors the
/// <c>DbcErrorCode</c> pattern introduced in v1.6.6 PATCH for consistency.
/// </summary>
public enum OdxErrorCode
{
    /// <summary>No error — successful import path.</summary>
    None = 0,

    /// <summary>The supplied .odx / .pdx file path does not exist on disk.</summary>
    FileNotFound,

    /// <summary>XML parsing failed (malformed .odx / corrupt .pdx zip).</summary>
    ParseError,

    /// <summary>ODX schema version outside accepted 2.0.0 + 2.2.0 range.</summary>
    UnsupportedVersion,

    /// <summary>
    /// Refused to import — counts exceeded 10000 items per type
    /// (D6 spec DoS guard).
    /// </summary>
    Refused,

    /// <summary>
    /// Duplicate DID / Routine / DTC identifier encountered across
    /// multiple ODX files in the same import; last-wins policy applied.
    /// </summary>
    DuplicateId,
}
