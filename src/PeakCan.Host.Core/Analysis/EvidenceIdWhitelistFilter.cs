namespace PeakCan.Host.Core.Analysis;

/// <summary>v3.54.0 MINOR: filters LLM-cited evidence IDs to only those
/// in the input session. Per v3.52.0 hard-boundary #13 (sister of DeepSeek
/// systematic bias insight from aspice-toolkit): drop invalid ID references
/// AND their associated claims. Only set Error when ALL claims are dropped
/// (whitelist filter, not whole-response reject).
/// <para>
/// v3.61.0 PATCH OPT-020: known limitation — this only filters the
/// <see cref="LlmAnalysisResult.AttributedEvidenceIds"/> list, not the
/// <c>Summary</c> free-text. If the LLM mentions an invalid ID in the
/// summary text itself (e.g. "as shown in E-9999"), that mention is NOT
/// removed. UI layer should consider post-processing the summary with a
/// regex to strip invalid ID references for safety-adjacent presentations.
/// </para></summary>
public static class EvidenceIdWhitelistFilter
{
    public static LlmAnalysisResult Filter(AnalysisSession session, LlmAnalysisResult raw)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(raw);

        // Build valid ID set (trim + case-sensitive preserve per hard-boundary #17)
        var validIds = new HashSet<string>(
            session.Report.Evidence.Select(e => e.EvidenceId.Trim()),
            StringComparer.Ordinal);

        // Filter AttributedEvidenceIds (trim for comparison, return trimmed form)
        var filteredIds = (raw.AttributedEvidenceIds ?? Array.Empty<string>())
            .Select(id => id.Trim())
            .Where(id => validIds.Contains(id))
            .ToList();

        // If all filtered out, return error envelope
        if (filteredIds.Count == 0 && (raw.AttributedEvidenceIds?.Count ?? 0) > 0)
        {
            return raw with
            {
                AttributedEvidenceIds = Array.Empty<string>(),
                Error = "DeepSeek response cited no valid evidence IDs; falling back to local-only",
            };
        }

        return raw with { AttributedEvidenceIds = filteredIds };
    }
}