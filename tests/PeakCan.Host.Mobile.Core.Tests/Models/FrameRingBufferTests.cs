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
