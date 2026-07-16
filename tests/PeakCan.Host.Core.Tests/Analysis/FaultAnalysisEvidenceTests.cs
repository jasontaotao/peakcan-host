using FluentAssertions;
using PeakCan.Host.Core.Analysis;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis;

public class FaultAnalysisEvidenceTests
{
    [Fact]
    public void Constructor_EvidenceId_MonotonicAndUnique()
    {
        var e1 = new FaultAnalysisEvidence(
            EvidenceId: "E-0001",
            SignalKey: "0x100.EngineRPM.asc-1",
            SourceId: "asc-1",
            Type: "state-transition",
            TimestampSeconds: 1.234,
            Value: 1.0,
            EnumText: "Active",
            Description: "BmsFaultState → Active");

        var e2 = new FaultAnalysisEvidence(
            EvidenceId: "E-0002",
            SignalKey: "0x101.InverterReady.asc-1",
            SourceId: "asc-1",
            Type: "delta-spike",
            TimestampSeconds: 1.300,
            Value: -1.0,
            EnumText: null,
            Description: "InverterReady fell edge");

        e1.EvidenceId.Should().Be("E-0001");
        e2.EvidenceId.Should().Be("E-0002");
        e1.EnumText.Should().Be("Active");
        e2.EnumText.Should().BeNull();
    }
}