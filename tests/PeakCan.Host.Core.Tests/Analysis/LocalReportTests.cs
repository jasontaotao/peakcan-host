using FluentAssertions;
using PeakCan.Host.Core.Analysis;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis;

public class LocalReportTests
{
    // CA1861: avoid allocating the same array on every call.
    private static readonly string[] NoFramesNotes = { "no frames in window" };
    private static readonly CandidateSignal[] OneCandidate =
    {
        new CandidateSignal(
            SignalKey: "0x101.InverterReady.asc-1",
            SourceId: "asc-1",
            Score: 0.92,
            ReasonText: "Δ=-1 (Ready→NotReady) 116ms before fault",
            EvidenceIds: new[] { "E-0002" }),
    };

    [Fact]
    public void Constructor_EmptyCandidates_SummaryIndicatesNoFinding()
    {
        // Per spec D4: "未发现可靠关联" 是合法输出，不是 error state
        var report = new LocalReport(
            Evidence: Array.Empty<FaultAnalysisEvidence>(),
            Candidates: Array.Empty<CandidateSignal>(),
            DataQualityNotes: NoFramesNotes,
            Summary: "未发现可靠关联（仅本地特征）",
            GeneratedAtUtc: new DateTime(2026, 7, 16, 12, 0, 0, DateTimeKind.Utc));

        report.Summary.Should().Contain("未发现可靠关联");
        report.Candidates.Should().BeEmpty();
    }

    [Fact]
    public void Constructor_WithCandidates_PopulatesFields()
    {
        var report = new LocalReport(
            Evidence: Array.Empty<FaultAnalysisEvidence>(),
            Candidates: OneCandidate,
            DataQualityNotes: Array.Empty<string>(),
            Summary: "本地推断（无归因）：1 candidate",
            GeneratedAtUtc: DateTime.UtcNow);

        report.Candidates.Should().HaveCount(1);
        report.Candidates[0].Score.Should().Be(0.92);
    }
}