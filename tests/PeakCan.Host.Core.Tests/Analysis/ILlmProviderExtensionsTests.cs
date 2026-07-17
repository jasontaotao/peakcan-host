using FluentAssertions;
using PeakCan.Host.Core.Analysis;

namespace PeakCan.Host.Core.Tests.Analysis;

public class ILlmProviderExtensionsTests
{
    // CA1861: avoid allocating the same array on every call.
    private static readonly string[] TwoEvidenceIds = { "E-0001", "E-0002" };
    private static readonly AnchoredSignalValue[] NoAnchoredSignals = Array.Empty<AnchoredSignalValue>();
    private static readonly FaultAnalysisEvidence[] NoEvidence = Array.Empty<FaultAnalysisEvidence>();
    private static readonly CandidateSignal[] NoCandidates = Array.Empty<CandidateSignal>();
    private static readonly string[] NoDataQualityNotes = Array.Empty<string>();

    /// <summary>
    /// Concrete ILlmProvider that does NOT override AnalyzeStreamingAsync —
    /// this is the case that exercises the default-interface-method
    /// fallback (single-shot path).
    /// </summary>
    internal sealed class SingleShotOnlyProvider : ILlmProvider
    {
        public string DisplayName => "SingleShotOnly";
        public LlmAnalysisResult? StubResult { get; init; }

        public Task<LlmAnalysisResult> AnalyzeAsync(AnalysisSession session, CancellationToken ct)
            => Task.FromResult(StubResult ?? new LlmAnalysisResult(
                Summary: "single-shot-summary",
                AttributedEvidenceIds: Array.Empty<string>(),
                RawResponseJson: "{}",
                Error: null));
    }

    [Fact]
    public async Task AnalyzeStreamingFromSingleShot_EmitsOneFinalResult()
    {
        var expectedResult = new LlmAnalysisResult(
            Summary: "test-summary",
            AttributedEvidenceIds: TwoEvidenceIds,
            RawResponseJson: "{}",
            Error: null);

        ILlmProvider provider = new SingleShotOnlyProvider { StubResult = expectedResult };

        var session = new AnalysisSession(
            SessionId: Guid.NewGuid(),
            Version: 1,
            FaultEvent: new FaultEvent(0.0, TimeSpan.Zero, TimeSpan.Zero, "", DateTime.UtcNow),
            AnchorSnapshot: new AnchorSnapshot(0.0, 0.0, NoAnchoredSignals, DateTime.UtcNow, 1),
            Report: new LocalReport(NoEvidence, NoCandidates, NoDataQualityNotes, "", DateTime.UtcNow),
            CreatedAtUtc: DateTime.UtcNow);

        var emitted = new List<LlmPartialUpdate>();
        // Call through the interface so the default interface method
        // (which delegates to ILlmProviderExtensions.AnalyzeStreamingFromSingleShot)
        // is dispatched by the C# compiler. Calling on the concrete type
        // bypasses the default-method dispatch.
        await foreach (var update in ((ILlmProvider)provider).AnalyzeStreamingAsync(session, CancellationToken.None))
        {
            emitted.Add(update);
        }

        emitted.Should().HaveCount(1);
        emitted[0].Should().BeOfType<LlmPartialUpdate.FinalResult>();
        var finalResult = ((LlmPartialUpdate.FinalResult)emitted[0]).Result;
        finalResult.Should().BeSameAs(expectedResult);
    }
}