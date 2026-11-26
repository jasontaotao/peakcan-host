namespace PeakCan.Host.Core.Analysis.Uds;

/// <summary>
/// Why a DID / routine could not be fully explained. Every value must reach the
/// report: "no ODX loaded" and "ODX loaded but the definition is absent" are
/// different operator problems and must not collapse into one blank cell.
/// </summary>
public enum UdsDegradeReason
{
    /// <summary>No ODX imported and no user JSON definition for this id.</summary>
    NoOdx,
    /// <summary>An ODX was imported, but it contains no definition for this id.</summary>
    OdxMissingDefinition,
    /// <summary>A definition exists but carries no field table (built-in / plain JSON).</summary>
    NoFieldTable,
    /// <summary>Payload is shorter than the definition requires.</summary>
    PayloadTooShort,
    /// <summary>Data is present but internally inconsistent (e.g. DID echo mismatch).</summary>
    IncompleteData,
}
