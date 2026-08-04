using PeakCan.HIL.Core.HIL.Diff;
using Xunit;

namespace PeakCan.HIL.Core.Tests.HIL.Diff;

public class DiffEngineTests
{
    private static CanFrame Frame(uint id, byte[] data) =>
        new(new CanId(id, FrameFormat.Standard), data, FrameFlags.None, default, default);

    [Fact]
    public void FrameLevel_ExactMatch_IdenticalSequences_IsMatch()
    {
        var engine = new DiffEngine();
        var frames = new[] { Frame(0x100, new byte[] { 1, 2, 3 }), Frame(0x200, new byte[] { 4, 5, 6 }) };

        var result = engine.Diff(frames, frames, new DiffConfig());

        Assert.True(result.IsMatch);
        Assert.Equal(2, result.Matched);
        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Removed);
        Assert.Equal(0, result.Modified);
    }

    [Fact]
    public void FrameLevel_EmptySequences_IsMatch()
    {
        var engine = new DiffEngine();
        var result = engine.Diff(Array.Empty<CanFrame>(), Array.Empty<CanFrame>(), new DiffConfig());

        Assert.True(result.IsMatch);
        Assert.Equal(0, result.TotalGolden);
        Assert.Equal(0, result.TotalActual);
    }

    [Fact]
    public void FrameLevel_OneModified_ModifiedEquals1()
    {
        var engine = new DiffEngine();
        var golden = new[] { Frame(0x100, new byte[] { 1, 2, 3 }) };
        var actual = new[] { Frame(0x100, new byte[] { 1, 2, 99 }) };

        var result = engine.Diff(golden, actual, new DiffConfig());

        Assert.False(result.IsMatch);
        Assert.Equal(1, result.Modified);
    }

    [Fact]
    public void FrameLevel_ActualExtraFrame_AddedEquals1()
    {
        var engine = new DiffEngine();
        var golden = new[] { Frame(0x100, new byte[] { 1 }) };
        var actual = new[] { Frame(0x100, new byte[] { 1 }), Frame(0x200, new byte[] { 2 }) };

        var result = engine.Diff(golden, actual, new DiffConfig());

        Assert.Equal(1, result.Matched);
        Assert.Equal(1, result.Added);
    }

    [Fact]
    public void FrameLevel_GoldenExtraFrame_RemovedEquals1()
    {
        var engine = new DiffEngine();
        var golden = new[] { Frame(0x100, new byte[] { 1 }), Frame(0x200, new byte[] { 2 }) };
        var actual = new[] { Frame(0x100, new byte[] { 1 }) };

        var result = engine.Diff(golden, actual, new DiffConfig());

        Assert.Equal(1, result.Matched);
        Assert.Equal(1, result.Removed);
    }

    [Fact]
    public void Validate_NearestNeighborZeroWindow_ThrowsAtDiffEntry()
    {
        var engine = new DiffEngine();
        var config = new DiffConfig(Alignment: AlignStrategy.NearestNeighbor, NeighborWindowMs: 0);

        Assert.Throws<ArgumentException>(() =>
            engine.Diff(Array.Empty<CanFrame>(), Array.Empty<CanFrame>(), config));
    }

    [Fact]
    public void SignalLevel_WithoutDbcLookup_ThrowsInvalidOperationException()
    {
        var engine = new DiffEngine(); // No IDbcLookup
        var config = new DiffConfig(Granularity: DiffGranularity.Signal);

        Assert.Throws<InvalidOperationException>(() =>
            engine.Diff(Array.Empty<CanFrame>(), Array.Empty<CanFrame>(), config));
    }

    [Fact]
    public void MatchRate_Correct()
    {
        var engine = new DiffEngine();
        var golden = new[] { Frame(0x100, new byte[] { 1 }), Frame(0x200, new byte[] { 2 }) };
        var actual = new[] { Frame(0x100, new byte[] { 1 }), Frame(0x300, new byte[] { 3 }) };

        var result = engine.Diff(golden, actual, new DiffConfig());

        Assert.Equal(0.5, result.MatchRate);
    }
}
