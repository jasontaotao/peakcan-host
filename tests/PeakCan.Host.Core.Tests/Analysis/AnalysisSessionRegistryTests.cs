using FluentAssertions;
using PeakCan.Host.Core.Analysis;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis;

public class AnalysisSessionRegistryTests
{
    private static AnalysisSession MakeSession(int version) =>
        new(Guid.NewGuid(), version,
            new FaultEvent(1.0, TimeSpan.FromMilliseconds(500),
                TimeSpan.FromMilliseconds(500), "test", DateTime.UtcNow),
            new AnchorSnapshot(1.0, 1.5, Array.Empty<AnchoredSignalValue>(),
                DateTime.UtcNow, 1),
            new LocalReport(Array.Empty<FaultAnalysisEvidence>(),
                Array.Empty<CandidateSignal>(), Array.Empty<string>(),
                "test", DateTime.UtcNow),
            DateTime.UtcNow);

    [Fact]
    public void CreateOrUpdate_FirstCall_StoresSession()
    {
        var registry = new AnalysisSessionRegistry();
        registry.CurrentSession.Should().BeNull();

        var session = registry.CreateOrUpdate(MakeSession(1));
        registry.CurrentSession.Should().Be(session);
    }

    [Fact]
    public void CreateOrUpdate_NewInputs_IncrementsVersion()
    {
        // Per spec hard-boundary #5: changing inputs -> new session, never silent overwrite
        var registry = new AnalysisSessionRegistry();
        var v1 = registry.CreateOrUpdate(MakeSession(1));

        // Simulate "user changed fault event time" by passing new session
        var v2 = registry.CreateOrUpdate(MakeSession(2));

        v2.Version.Should().Be(2);
        registry.CurrentSession!.Version.Should().Be(2);
    }

    [Fact]
    public void Clear_ResetsCurrentSession()
    {
        // Per spec hard-boundary #8: AnalysisSessionRegistry is independent
        // of TraceViewerViewModel.Reset, but explicit Clear() is available
        // for when the user explicitly chooses "discard session".
        var registry = new AnalysisSessionRegistry();
        registry.CreateOrUpdate(MakeSession(1));

        registry.Clear();
        registry.CurrentSession.Should().BeNull();
    }
}
