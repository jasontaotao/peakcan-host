namespace PeakCan.Host.Core.Uds.Odx;

/// <summary>
/// ODX <c>DIAG-SERVICE</c> element. The smallest interaction unit
/// inside a DIAG-LAYER. Per ISO 22901 v2.0.0, the parser only
/// consumes the subset of attributes documented here; unknown
/// attributes are silently skipped.
/// </summary>
public sealed record DiagService(
    /// <summary>Odx id attribute (e.g., "svc.read_vin").</summary>
    string Id,

    /// <summary>Odx short-name attribute (e.g., "ReadDataByIdentifier_VIN").</summary>
    string ShortName,

    /// <summary>
    /// Id-ref attribute pointing into the
    /// <c>REQUEST-REF</c> element (the DOP id the service operates on).
    /// </summary>
    string RequestRefId);
