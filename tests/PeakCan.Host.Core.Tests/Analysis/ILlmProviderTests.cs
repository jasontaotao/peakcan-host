using FluentAssertions;
using PeakCan.Host.Core.Analysis;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis;

public class ILlmProviderTests
{
    [Fact]
    public void NotImplementedLlmProvider_DisplayName_IndicatesP0LocalOnly()
    {
        var provider = new NotImplementedLlmProvider();
        provider.DisplayName.Should().Contain("P0 local-only");
    }

    [Fact]
    public async Task NotImplementedLlmProvider_AnalyzeAsync_ThrowsNotImplemented()
    {
        var provider = new NotImplementedLlmProvider();
        var session = new AnalysisSession(Guid.NewGuid(), 1,
            new FaultEvent(1.0, TimeSpan.FromMilliseconds(500),
                TimeSpan.FromMilliseconds(500), "test", DateTime.UtcNow),
            new AnchorSnapshot(1.0, 1.5, Array.Empty<AnchoredSignalValue>(),
                DateTime.UtcNow, 1),
            new LocalReport(Array.Empty<FaultAnalysisEvidence>(),
                Array.Empty<CandidateSignal>(), Array.Empty<string>(),
                "test", DateTime.UtcNow),
            DateTime.UtcNow);

        await Assert.ThrowsAsync<NotImplementedException>(
            () => provider.AnalyzeAsync(session, CancellationToken.None));
    }
}
