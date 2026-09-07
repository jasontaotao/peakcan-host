using FluentAssertions;
using PeakCan.Host.Mobile.Core.Models;
using Xunit;

namespace PeakCan.Host.Mobile.Core.Tests.Models;

public class FrameRingBufferTests
{
    private static readonly double[] ExpectedUnderCapacity = [1d, 2d, 3d];
    private static readonly double[] ExpectedOverCapacity = [3d, 4d, 5d];

    private static FrameRow Row(int i) => new(i, (uint)i, false, 0, Array.Empty<byte>());

    [Fact]
    public void Snapshot_ReturnsFramesInInsertionOrder_WhenUnderCapacity()
    {
        var buf = new FrameRingBuffer(8);
        buf.Add(Row(1)); buf.Add(Row(2)); buf.Add(Row(3));
        var snap = buf.Snapshot();
        snap.Should().HaveCount(3);
        snap.Select(r => r.Timestamp).Should().BeEquivalentTo(ExpectedUnderCapacity);
    }

    [Fact]
    public void Add_OverwritesOldest_WhenAtCapacity()
    {
        var buf = new FrameRingBuffer(3);
        for (int i = 1; i <= 5; i++) buf.Add(Row(i)); // 容量 3 → 留 3,4,5
        var snap = buf.Snapshot();
        snap.Should().HaveCount(3);
        snap.Select(r => r.Timestamp).Should().BeEquivalentTo(ExpectedOverCapacity);
    }

    [Fact]
    public void CopyLatest_CopiesInsertionOrderWithoutAllocatingSnapshot()
    {
        var buf = new FrameRingBuffer(4);
        buf.Add(Row(1)); buf.Add(Row(2)); buf.Add(Row(3));
        var destination = new FrameRow[2];

        var copied = buf.CopyLatest(destination);

        copied.Should().Be(2);
        destination.Select(r => r.Timestamp).Should().Equal(2d, 3d);
    }

    [Fact]
    public void CopyLatest_AfterWrap_ReturnsLatestRows()
    {
        var buf = new FrameRingBuffer(3);
        for (var i = 1; i <= 5; i++) buf.Add(Row(i));
        var destination = new FrameRow[4];

        var copied = buf.CopyLatest(destination);

        copied.Should().Be(3);
        destination.Take(copied).Select(r => r.Timestamp).Should().Equal(3d, 4d, 5d);
    }
    [Fact]
    public void Clear_ResetsBuffer()
    {
        var buf = new FrameRingBuffer(4);
        buf.Add(Row(1)); buf.Add(Row(2));
        buf.Clear();
        buf.Snapshot().Should().BeEmpty();
        buf.Count.Should().Be(0);
    }

    [Fact]
    public void Constructor_RejectsNonPositiveCapacity()
    {
        Action act = () => _ = new FrameRingBuffer(0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
