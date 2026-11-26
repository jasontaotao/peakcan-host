namespace PeakCan.Host.Core.Analysis.Uds;

/// <summary>How a request's exchange ended. <see cref="None"/> = still open at log end.</summary>
public enum UdsFinalKind
{
    Positive,
    Negative,
    None,
}

/// <summary>
/// One request paired with the responses seen for it. <see cref="IsAmbiguous"/>
/// is set when two same-SID requests were open at once or a duplicate final
/// response arrived — the pairing is then a best guess, and the report says so
/// instead of presenting it as fact.
/// </summary>
public sealed record UdsOfflineTransaction(
    UdsOfflinePdu Request,
    IReadOnlyList<UdsOfflinePdu> Responses,
    UdsFinalKind FinalKind,
    int PendingCount,
    byte? FinalNrc,
    bool IsOrphan,
    bool IsAmbiguous,
    bool HasDuplicateFinalResponse,
    IReadOnlyList<string> Unknowns)
{
    public int EvidenceIndex => Request.FirstEvidenceIndex;
    public double FirstTimestamp => Request.FirstTimestamp;
}

/// <summary>
/// Pairing output. The three failure lists are the transport-valid PDUs that
/// could not be paired or did not pass the echo checks; the report turns them
/// into noise rows with explicit reasons.
/// </summary>
public sealed record UdsTransactionBuildResult(
    IReadOnlyList<UdsOfflineTransaction> Transactions,
    IReadOnlyList<UdsOfflinePdu> OrphanResponses,
    IReadOnlyList<UdsOfflinePdu> ValidationFailures,
    IReadOnlyList<UdsOfflinePdu> DuplicateFinalResponses)
{
    public IReadOnlyList<string> FailedChecks { get; init; } = Array.Empty<string>();
}
