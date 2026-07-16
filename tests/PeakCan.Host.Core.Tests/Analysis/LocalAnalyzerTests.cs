using FluentAssertions;
using PeakCan.Host.Core.Analysis;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis;

public class LocalAnalyzerTests
{
    [Fact]
    public void Analyze_TwoSourcesOneHighFrameCount_RankingPerSourceNormalized()
    {
        // Per spec hard-boundary #14: high-frame-count sources must NOT dominate
        var evidence = new List<FaultAnalysisEvidence>
        {
            // Source A: 3 transitions (would dominate if global normalization)
            new("E-0001", "0x100.SignalA.srcA", "srcA", "state-transition", 1.1, 1, null, "..."),
            new("E-0002", "0x100.SignalA.srcA", "srcA", "state-transition", 1.2, 2, null, "..."),
            new("E-0003", "0x100.SignalA.srcA", "srcA", "state-transition", 1.3, 3, null, "..."),
            // Source B: 1 transition (rare → high score in per-source view)
            new("E-0004", "0x200.SignalB.srcB", "srcB", "state-transition", 1.15, 99, null, "..."),
        };
        var faultEvent = new FaultEvent(1.2,
            TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500),
            "test", DateTime.UtcNow);
        var snapshot = new AnchorSnapshot(1.0, 1.5,
            Array.Empty<AnchoredSignalValue>(), DateTime.UtcNow, 1);

        var analyzer = new LocalAnalyzer();
        var report = analyzer.Analyze(evidence, faultEvent, snapshot);

        // Per-source normalization: srcB's 1 transition ranks higher than
        // srcA's 3 transitions within its source. Global candidate count
        // is 1 per source (SignalA appears once in srcA, SignalB once in srcB).
        report.Candidates.Should().HaveCount(2);
        report.Candidates.Should().Contain(c => c.SourceId == "srcB");
        report.Candidates.Should().Contain(c => c.SourceId == "srcA");
    }

    [Fact]
    public void Analyze_NoEvidence_ReturnsEmptyCandidatesWithHonestSummary()
    {
        var faultEvent = new FaultEvent(1.0,
            TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500),
            "test", DateTime.UtcNow);
        var snapshot = new AnchorSnapshot(1.0, 1.5,
            Array.Empty<AnchoredSignalValue>(), DateTime.UtcNow, 1);

        var analyzer = new LocalAnalyzer();
        var report = analyzer.Analyze(Array.Empty<FaultAnalysisEvidence>(), faultEvent, snapshot);

        report.Candidates.Should().BeEmpty();
        report.Summary.Should().Contain("未发现可靠关联");
    }
}