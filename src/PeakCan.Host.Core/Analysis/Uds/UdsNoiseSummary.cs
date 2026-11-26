namespace PeakCan.Host.Core.Analysis.Uds;

/// <summary>Aggregated non-diagnostic traffic for one CAN id.</summary>
public sealed record UdsNoiseIdCount(
    uint Id,
    bool IsExtended,
    int Count,
    int FirstEvidenceIndex,
    int LastEvidenceIndex);

/// <summary>How many address-matched frames failed for a given transport reason.</summary>
public sealed record UdsNoiseReasonCount(UdsNoiseReason Reason, int Count);

/// <summary>
/// Report-level noise picture. <see cref="TopIds"/> is capped (20) so a busy
/// bus log stays renderable; <see cref="AggregationTruncated"/> tells the reader
/// the list is not exhaustive, and <see cref="DistinctNonDiagnosticIds"/> gives
/// the real breadth without materialising it.
/// </summary>
public sealed record UdsNoiseSummary(
    IReadOnlyList<UdsNoiseIdCount> TopIds,
    IReadOnlyList<UdsNoiseReasonCount> Reasons,
    int ResponseValidationFailed,
    int DuplicateFinalResponses,
    bool AggregationTruncated,
    int DistinctNonDiagnosticIds);
