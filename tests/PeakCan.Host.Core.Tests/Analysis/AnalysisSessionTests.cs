using FluentAssertions;
using PeakCan.Host.Core.Analysis;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis;

public class AnalysisSessionTests
{
    [Fact]
    public void Constructor_AllFieldsPopulated()
    {
        var faultEvent = new FaultEvent(1.234, TimeSpan.FromMilliseconds(500),
            TimeSpan.FromMilliseconds(500), "test", DateTime.UtcNow);
        var snapshot = new AnchorSnapshot(1.0, 1.5,
            Array.Empty<AnchoredSignalValue>(), DateTime.UtcNow, 1);
        var report = new LocalReport(Array.Empty<FaultAnalysisEvidence>(),
            Array.Empty<CandidateSignal>(), Array.Empty<string>(),
            "未发现可靠关联", DateTime.UtcNow);

        var session = new AnalysisSession(
            SessionId: Guid.NewGuid(),
            Version: 1,
            FaultEvent: faultEvent,
            AnchorSnapshot: snapshot,
            Report: report,
            CreatedAtUtc: DateTime.UtcNow);

        session.Version.Should().Be(1);
        session.FaultEvent.Should().Be(faultEvent);
        session.AnchorSnapshot.Should().Be(snapshot);
    }
}