namespace PeakCan.Host.Core.Analysis;

/// <summary>v3.52.0 MINOR (P0 stub) / P1 PATCH (concrete): LLM provider output.
/// Summary is the LLM's natural-language summary. AttributedEvidenceIds is
/// the whitelist-filtered list of Evidence IDs the LLM actually cited —
/// entries not in the input session's Evidence list are dropped per
/// hard-boundary #13. RawResponseJson is the verbatim provider response
/// (without Authorization headers), used for diagnostics / replay. Error
/// is non-null when the provider failed (401/429/timeout/JSON-parse) —
/// callers should fall back to LocalReport and surface Error in the UI.</summary>
public sealed record LlmAnalysisResult(
    string Summary,
    IReadOnlyList<string> AttributedEvidenceIds,
    string RawResponseJson,
    string? Error);
