using FluentAssertions;
using NSubstitute;
using PeakCan.Host.Core.Analysis;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis;

public class EvidenceExtractorTests
{
    private static ReplayFrame Frame(double t, uint id, byte dlc, params byte[] data)
        => new(t, id, dlc, data, FrameFlags.None);

    private static AnchoredSignalValue Signal(string sourceId)
        => new(
            SignalKey: $"0x100.EngineRPM.{sourceId}",
            SourceId: sourceId,
            LatestValue: 0,
            BlueLatestValue: 0,
            DeltaValue: 0,
            LatestText: string.Empty,
            BlueText: string.Empty,
            DeltaText: string.Empty);

    [Fact]
    public void Extract_PerSource_IndependentWindowCropping()
    {
        // Two sources, different frame counts, same window
        var frameSource = Substitute.For<IFrameSourceProvider>();
        var framesA = new[]
        {
            Frame(1.0, 0x100, 8, 0x10, 0, 0, 0, 0, 0, 0, 0),
            Frame(1.2, 0x100, 8, 0x20, 0, 0, 0, 0, 0, 0, 0),
            Frame(1.4, 0x100, 8, 0x00, 0, 0, 0, 0, 0, 0, 0),
            Frame(1.6, 0x100, 8, 0x00, 0, 0, 0, 0, 0, 0, 0),
        };
        var framesB = new[]
        {
            Frame(1.1, 0x100, 8, 0x55, 0, 0, 0, 0, 0, 0, 0),
            Frame(1.5, 0x100, 8, 0x66, 0, 0, 0, 0, 0, 0, 0),
        };
        frameSource.GetFrames("asc-A").Returns(framesA);
        frameSource.GetFrames("asc-B").Returns(framesB);

        var faultEvent = new FaultEvent(1.3,
            TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200),
            "test", DateTime.UtcNow);
        // Snapshot signals drive per-source iteration (per hard-boundary #6):
        // EvidenceExtractor walks each SourceId found in the anchor snapshot.
        var snapshot = new AnchorSnapshot(1.0, 1.5,
            new[] { Signal("asc-A"), Signal("asc-B") }, DateTime.UtcNow, 1);

        var extractor = new EvidenceExtractor();
        var evidence = extractor.Extract(faultEvent, snapshot, frameSource,
            dbc: null, dbcIdToSourceIdMap: new Dictionary<uint, string>
            {
                [0x100] = "0x100.EngineRPM",
            });

        // Both sources contribute; per-source evidence independent
        evidence.Should().Contain(e => e.SourceId == "asc-A");
        evidence.Should().Contain(e => e.SourceId == "asc-B");
    }

    [Fact]
    public void Extract_EmptySource_ReturnsEmptyList_NoThrow()
    {
        var frameSource = Substitute.For<IFrameSourceProvider>();
        frameSource.GetFrames("empty").Returns(Array.Empty<ReplayFrame>());

        var faultEvent = new FaultEvent(1.0,
            TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500),
            "test", DateTime.UtcNow);
        var snapshot = new AnchorSnapshot(1.0, 1.5,
            new[] { Signal("empty") }, DateTime.UtcNow, 1);

        var extractor = new EvidenceExtractor();
        var evidence = extractor.Extract(faultEvent, snapshot, frameSource,
            dbc: null, dbcIdToSourceIdMap: new Dictionary<uint, string>());

        evidence.Should().BeEmpty();
    }
}
