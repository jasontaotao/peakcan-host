namespace PeakCan.Host.Core.Uds.Odx;

/// <summary>
/// Outcome of an ODX / PDX import operation. Non-throwing by design —
/// exceptions during parse are caught and surfaced via
/// <see cref="HasError"/> + <see cref="ErrorCode"/> +
/// <see cref="ErrorMessage"/> so the UI never crashes mid-import.
/// </summary>
public sealed record OdxImportResult(
    int DidCount,
    int RoutineCount,
    int DtcCount,
    IReadOnlyList<string> Warnings,
    bool HasError,
    OdxErrorCode ErrorCode,
    string? ErrorMessage)
{
    /// <summary>
    /// Build a successful import result with counts and warnings.
    /// </summary>
    public static OdxImportResult Ok(
        int dids,
        int routines,
        int dtcs,
        IReadOnlyList<string> warnings)
        => new(
            DidCount: dids,
            RoutineCount: routines,
            DtcCount: dtcs,
            Warnings: warnings ?? Array.Empty<string>(),
            HasError: false,
            ErrorCode: OdxErrorCode.None,
            ErrorMessage: null);

    /// <summary>Build a failure result with no counts and a single message.</summary>
    public static OdxImportResult Failed(OdxErrorCode code, string message)
        => new(
            DidCount: 0,
            RoutineCount: 0,
            DtcCount: 0,
            Warnings: Array.Empty<string>(),
            HasError: true,
            ErrorCode: code,
            ErrorMessage: message);
}
