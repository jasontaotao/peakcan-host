namespace PeakCan.Host.Core.Uds.Odx;

/// <summary>
/// Root ODX document tree after deserialization from an
/// <c>XDocument</c>. Only the <c>DIAG-LAYER</c> subset is exposed —
/// COMPARAM-SPEC, ECU-CONFIG, and other top-level children are
/// ignored (out of scope per spec §10).
/// </summary>
public sealed record OdxDocument(
    /// <summary>Odx VERSION attribute (e.g., "2.0.0" or "2.2.0").</summary>
    string Version,

    /// <summary>
    /// All parsed DIAG-LAYER entries (BASE-VARIANT only).
    /// Empty list if no layers are present in the source.
    /// </summary>
    IReadOnlyList<DiagLayer> Layers);
