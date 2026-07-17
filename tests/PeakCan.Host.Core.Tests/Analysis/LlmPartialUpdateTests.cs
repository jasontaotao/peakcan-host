using FluentAssertions;
using PeakCan.Host.Core.Analysis;

namespace PeakCan.Host.Core.Tests.Analysis;

public class LlmPartialUpdateTests
{
    // CA1861: avoid allocating the same array on every call. Mirrors the
    // pattern used in LocalReportTests.cs.
    private static readonly string[] OneEvidenceId = { "E-0001" };
    private static readonly string[] EmptyEvidenceIds = Array.Empty<string>();

    [Fact]
    public void PartialSummary_CarriesDelta()
    {
        var update = new LlmPartialUpdate.PartialSummary("Hello");
        update.Delta.Should().Be("Hello");
    }

    [Fact]
    public void PartialEvidenceId_CarriesEvidenceId()
    {
        var update = new LlmPartialUpdate.PartialEvidenceId("E-0017");
        update.EvidenceId.Should().Be("E-0017");
    }

    [Fact]
    public void FinalResult_CarriesResult()
    {
        var result = new LlmAnalysisResult(
            Summary: "summary",
            AttributedEvidenceIds: OneEvidenceId,
            RawResponseJson: "{}",
            Error: null);
        var update = new LlmPartialUpdate.FinalResult(result);
        update.Result.Should().BeSameAs(result);
    }

    [Fact]
    public void Variants_ArePatternMatchable()
    {
        // Pin the discriminated-union shape: switch over the 3 variants
        // must handle each (no warning for missing cases).
        var updates = new LlmPartialUpdate[]
        {
            new LlmPartialUpdate.PartialSummary("a"),
            new LlmPartialUpdate.PartialEvidenceId("E-1"),
            new LlmPartialUpdate.FinalResult(new LlmAnalysisResult("", EmptyEvidenceIds, "", null)),
        };

        var counts = new Dictionary<string, int>();
        foreach (var u in updates)
        {
            var key = u switch
            {
                LlmPartialUpdate.PartialSummary => "summary",
                LlmPartialUpdate.PartialEvidenceId => "id",
                LlmPartialUpdate.FinalResult => "final",
                _ => "unknown"
            };
            counts[key] = counts.GetValueOrDefault(key, 0) + 1;
        }

        counts["summary"].Should().Be(1);
        counts["id"].Should().Be(1);
        counts["final"].Should().Be(1);
    }
}