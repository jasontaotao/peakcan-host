using FluentAssertions;
using PeakCan.Host.Core.Analysis;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis;

public class AnchorSnapshotTests
{
    [Fact]
    public void Constructor_DualAnchors_PopulatesAllFields()
    {
        // Arrange
        var signals = new List<AnchoredSignalValue>
        {
            new("0x100.EngineRPM.asc-1", "asc-1", 1500.0, 2080.0, 580.0,
                "1500.00", "2080.00", "580.00"),
        };

        // Act
        var snap = new AnchorSnapshot(
            GreenTimestampSeconds: 1.234,
            BlueTimestampSeconds: 1.350,
            Signals: signals,
            CapturedAtUtc: new DateTime(2026, 7, 16, 12, 0, 0, DateTimeKind.Utc),
            Version: 1);

        // Assert
        snap.GreenTimestampSeconds.Should().Be(1.234);
        snap.BlueTimestampSeconds.Should().Be(1.350);
        snap.Signals.Should().HaveCount(1);
        snap.Signals[0].DeltaValue.Should().Be(580.0);
        snap.Version.Should().Be(1);
    }

    [Fact]
    public void AnchoredSignalValue_SignalKey_Format_IsIdHexDotSignalNameDotSourceId()
    {
        // Per spec hard-boundary #7: SignalKey = {idHex}.{signalName}[.{sourceId}]
        var key = "0x100.EngineRPM.asc-1";
        var sig = new AnchoredSignalValue(key, "asc-1", 1.0, 2.0, 1.0, "1", "2", "1");
        sig.SignalKey.Should().Be(key);
    }
}