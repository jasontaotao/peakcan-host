namespace PeakCan.Host.Core.Analysis;

/// <summary>v3.52.0 MINOR: produces LocalReport from extracted evidence.
/// Per hard-boundary #14: per-source normalization — score each candidate
/// within its source's own distribution, then rank across sources by the
/// normalized score. A source with 100 transitions does NOT dominate a
/// source with 1 rare transition. Per spec D4: when no candidate scores
/// above threshold, Summary = "未发现可靠关联" — a legal output, not error.</summary>
public sealed class LocalAnalyzer
{
    private const double CandidateThreshold = 0.1;
    private const string NoAttributionSummary = "未发现可靠关联（仅本地特征；无 LLM 归因）";

    public LocalReport Analyze(
        IReadOnlyList<FaultAnalysisEvidence> evidence,
        FaultEvent faultEvent,
        AnchorSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(faultEvent);
        ArgumentNullException.ThrowIfNull(snapshot);

        if (evidence.Count == 0)
        {
            return new LocalReport(
                Evidence: evidence,
                Candidates: Array.Empty<CandidateSignal>(),
                DataQualityNotes: new[] { "no evidence extracted within window" },
                Summary: NoAttributionSummary,
                GeneratedAtUtc: DateTime.UtcNow);
        }

        // Per-source grouping
        var bySource = evidence
            .GroupBy(e => e.SourceId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var candidates = new List<CandidateSignal>();
        var notes = new List<string>();

        foreach (var (sourceId, sourceEvidence) in bySource)
        {
            var perSignal = sourceEvidence
                .GroupBy(e => e.SignalKey)
                .Select(g => new
                {
                    SignalKey = g.Key,
                    SourceId = sourceId,
                    TransitionCount = g.Count(),
                    EvidenceIds = g.Select(e => e.EvidenceId).ToList(),
                    FirstTs = g.Min(e => e.TimestampSeconds),
                    LastTs = g.Max(e => e.TimestampSeconds),
                })
                .ToList();

            if (perSignal.Count == 0) continue;

            int maxInSource = perSignal.Max(p => p.TransitionCount);
            if (maxInSource == 0) continue;

            foreach (var sig in perSignal)
            {
                double score = (double)sig.TransitionCount / maxInSource;
                if (score < CandidateThreshold) continue;

                string reason = $"{sig.TransitionCount} state transitions in window "
                    + $"[{sig.FirstTs:F3}s .. {sig.LastTs:F3}s]";

                candidates.Add(new CandidateSignal(
                    SignalKey: sig.SignalKey,
                    SourceId: sourceId,
                    Score: score,
                    ReasonText: reason,
                    EvidenceIds: sig.EvidenceIds));
            }
        }

        candidates = candidates.OrderByDescending(c => c.Score).ToList();

        return new LocalReport(
            Evidence: evidence,
            Candidates: candidates,
            DataQualityNotes: notes,
            Summary: candidates.Count == 0
                ? NoAttributionSummary
                : $"本地推断（无归因）：{candidates.Count} candidate(s)",
            GeneratedAtUtc: DateTime.UtcNow);
    }
}