namespace PeakCan.Host.Core.Analysis;

/// <summary>v3.52.0 MINOR: the local-only deterministic analysis output.
/// Per spec D4: Summary MUST explicitly mark "no attribution" so the UI
/// can render a visual distinction vs an LLM-attributed report (P1 PATCH).
/// "未发现可靠关联" is a legal Summary value when no candidate scores
/// above the per-source normalization threshold.</summary>
public sealed record LocalReport(
    IReadOnlyList<FaultAnalysisEvidence> Evidence,
    IReadOnlyList<CandidateSignal> Candidates,
    IReadOnlyList<string> DataQualityNotes,
    string Summary,
    DateTime GeneratedAtUtc);

/// <summary>Per-signal candidate for association with the fault event.
/// Score is per-source-normalized in [0, 1] per hard-boundary #14
/// (high-frame-count sources must not dominate the global ranking).
/// EvidenceIds reference FaultAnalysisEvidence.EvidenceId within the same
/// LocalReport — UI uses them to navigate from candidate → evidence detail.</summary>
public sealed record CandidateSignal(
    string SignalKey,
    string SourceId,
    double Score,
    string ReasonText,
    IReadOnlyList<string> EvidenceIds);