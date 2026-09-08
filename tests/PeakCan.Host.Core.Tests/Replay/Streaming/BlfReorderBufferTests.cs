using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Replay.Streaming;

public class BlfReorderBufferTests
{
    private static ReplayFrame Frame(double timestamp, uint id = 1) =>
        new(timestamp, id, 1, new byte[] { 1 }, FrameFlags.None, false, 1);

    [Fact]
    public void Push_BuffersFramesWithinWindow()
    {
        var buffer = new BlfReorderBuffer();
        buffer.Push(Frame(0.1)).Should().BeEmpty();
        buffer.Push(Frame(0.9)).Should().BeEmpty();
        buffer.Flush().Select(f => f.Timestamp).Should().Equal(0.1, 0.9);
    }

    [Fact]
    public void Push_SortsLateArrivalWithinWindow()
    {
        var buffer = new BlfReorderBuffer();
        buffer.Push(Frame(0.9)).Should().BeEmpty();
        buffer.Push(Frame(0.1)).Should().BeEmpty();
        buffer.Flush().Select(f => f.Timestamp).Should().Equal(0.1, 0.9);
    }

    [Fact]
    public void Push_EmitsPreviousWindowSorted_WhenWindowAdvances()
    {
        var buffer = new BlfReorderBuffer();
        buffer.Push(Frame(0.9)).Should().BeEmpty();
        buffer.Push(Frame(0.2)).Should().BeEmpty();
        var ready = buffer.Push(Frame(1.4));
        ready.Select(f => f.Timestamp).Should().Equal(0.2, 0.9);
        buffer.Flush().Select(f => f.Timestamp).Should().Equal(1.4);
    }

    [Fact]
    public void Flush_AfterFlush_RestartsWindow()
    {
        var buffer = new BlfReorderBuffer();
        buffer.Push(Frame(0.1)).Should().BeEmpty();
        buffer.Flush().Should().HaveCount(1);
        buffer.Push(Frame(3.0)).Should().BeEmpty();
        buffer.Flush().Select(f => f.Timestamp).Should().Equal(3.0);
    }
}
