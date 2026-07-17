using FluentAssertions;
using PeakCan.Host.Core.Analysis;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis;

public class EvidenceIdWhitelistFilterTests
{
    private static readonly string[] ValidIdsE1E2 = { "E-0001", "E-0002" };
    private static readonly string[] SingleE1 = { "E-0001" };
    private static readonly string[] InvalidE9999 = { "E-9999" };
    private static readonly string[] MixedE1AndE9999 = { "E-0001", "E-9999" };
    private static readonly string[] WhitespaceE1 = { "  E-0001  " };
    private static readonly string[] LowercaseE0001 = { "e-0001" };

    private static AnalysisSession MakeSession(params string[] validIds)
    {
        var evidence = validIds.Select((id, i) => new FaultAnalysisEvidence(
            EvidenceId: id, SignalKey: $"0x100.Signal{i}.src", SourceId: "src",
            Type: "state-transition", TimestampSeconds: 1.0 + i * 0.1, Value: 1,
            EnumText: null, Description: "test")).ToList();
        var report = new LocalReport(
            Evidence: evidence,
            Candidates: Array.Empty<CandidateSignal>(),
            DataQualityNotes: Array.Empty<string>(),
            Summary: "test report",
            GeneratedAtUtc: DateTime.UtcNow);
        return new AnalysisSession(
            SessionId: Guid.NewGuid(),
            Version: 1,
            FaultEvent: new FaultEvent(1.0, TimeSpan.FromMilliseconds(500),
                TimeSpan.FromMilliseconds(500), "test", DateTime.UtcNow),
            AnchorSnapshot: new AnchorSnapshot(1.0, 1.5,
                Array.Empty<AnchoredSignalValue>(), DateTime.UtcNow, 1),
            Report: report,
            CreatedAtUtc: DateTime.UtcNow);
    }

    [Fact]
    public void Filter_AllValidIds_PassesThrough()
    {
        var session = MakeSession("E-0001", "E-0002");
        var raw = new LlmAnalysisResult(
            Summary: "Both E-0001 and E-0002 indicate fault state",
            AttributedEvidenceIds: ValidIdsE1E2,
            RawResponseJson: "{\"summary\":\"...\",\"claims\":[{\"evidence_ids\":[\"E-0001\",\"E-0002\"],\"text\":\"both\"}]}",
            Error: null);

        var filtered = EvidenceIdWhitelistFilter.Filter(session, raw);
        filtered.AttributedEvidenceIds.Should().BeEquivalentTo(ValidIdsE1E2);
        filtered.Summary.Should().Be(raw.Summary);
        filtered.Error.Should().BeNull();
    }

    [Fact]
    public void Filter_AllInvalidIds_ReturnsErrorEnvelope()
    {
        var session = MakeSession("E-0001");
        var raw = new LlmAnalysisResult(
            Summary: "Cites E-9999 which is not in evidence",
            AttributedEvidenceIds: InvalidE9999,
            RawResponseJson: "{\"summary\":\"...\",\"claims\":[{\"evidence_ids\":[\"E-9999\"]}]}",
            Error: null);

        var filtered = EvidenceIdWhitelistFilter.Filter(session, raw);
        filtered.AttributedEvidenceIds.Should().BeEmpty();
        filtered.Error.Should().Contain("no valid evidence IDs");
    }

    [Fact]
    public void Filter_MixedValidAndInvalid_KeepsOnlyValidClaims()
    {
        var session = MakeSession("E-0001");
        var raw = new LlmAnalysisResult(
            Summary: "Mixed",
            AttributedEvidenceIds: MixedE1AndE9999,
            RawResponseJson: "{\"claims\":[{\"evidence_ids\":[\"E-0001\"],\"text\":\"valid\"},{\"evidence_ids\":[\"E-9999\"],\"text\":\"invalid\"}]}",
            Error: null);

        var filtered = EvidenceIdWhitelistFilter.Filter(session, raw);
        filtered.AttributedEvidenceIds.Should().BeEquivalentTo(SingleE1);
    }

    [Fact]
    public void Filter_EmptySessionEvidence_AllIdsInvalid()
    {
        var session = MakeSession();  // no evidence
        var raw = new LlmAnalysisResult(
            Summary: "Cites E-0001",
            AttributedEvidenceIds: SingleE1,
            RawResponseJson: "{}",
            Error: null);

        var filtered = EvidenceIdWhitelistFilter.Filter(session, raw);
        filtered.AttributedEvidenceIds.Should().BeEmpty();
        filtered.Error.Should().NotBeNull();
    }

    [Fact]
    public void Filter_WhitespaceInIds_TrimsBeforeComparison()
    {
        var session = MakeSession("E-0001");
        var raw = new LlmAnalysisResult(
            Summary: "test",
            AttributedEvidenceIds: WhitespaceE1,
            RawResponseJson: "{}",
            Error: null);

        var filtered = EvidenceIdWhitelistFilter.Filter(session, raw);
        filtered.AttributedEvidenceIds.Should().Contain("E-0001");
    }

    [Fact]
    public void Filter_CaseSensitive_PreservesCase()
    {
        var session = MakeSession("E-0001");
        var raw = new LlmAnalysisResult(
            Summary: "test",
            AttributedEvidenceIds: LowercaseE0001,  // lowercase
            RawResponseJson: "{}",
            Error: null);

        var filtered = EvidenceIdWhitelistFilter.Filter(session, raw);
        filtered.AttributedEvidenceIds.Should().BeEmpty();
    }
}