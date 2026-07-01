namespace PeakCan.Host.Core.Uds.Odx;

/// <summary>
/// ODX <c>DIAG-LAYER</c> element. Only <c>BASE-VARIANT</c> layers
/// are parsed in this implementation (D4 spec — CONDITIONAL
/// and ECU-VARIANT are deferred).
/// </summary>
public sealed record DiagLayer(
    /// <summary>Odx id attribute.</summary>
    string Id,

    /// <summary>Odx short-name attribute.</summary>
    string ShortName,

    /// <summary>All DIAG-SERVICE entries directly under this layer.</summary>
    IReadOnlyList<DiagService> Services);
