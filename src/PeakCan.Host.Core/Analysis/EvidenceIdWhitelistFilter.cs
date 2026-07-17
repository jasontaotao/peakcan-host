namespace PeakCan.Host.Core.Analysis;

/// <summary>v3.54.0 MINOR: filters LLM-cited evidence IDs to only those
/// in the input session. Per v3.52.0 hard-boundary #13 (sister of DeepSeek
/// systematic bias insight from aspice-toolkit): drop invalid ID references
/// AND their associated claims. Only set Error when ALL claims are dropped
/// (whitelist filter, not whole-response reject).</summary>
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