using FluentAssertions;
using PeakCan.Host.Core;
using Xunit;

namespace PeakCan.Host.Core.Tests;

public class CanFrameTests
{
    [Fact]
    public void IsFd_True_When_Fd_Flag_Set()
    {
        var frame = new CanFrame(
            new CanId(1, FrameFormat.Standard),
            new byte[] { 0xDE, 0xAD, 0xBE, 0xEF },
            FrameFlags.Fd | FrameFlags.BitRateSwitch,
            ChannelId.None,
            default);
        frame.IsFd.Should().BeTrue();
        frame.Dlc.Should().Be(4);
    }

    [Fact]
    public void IsError_True_When_ErrFrame_Set()
    {
        var frame = new CanFrame(
            new CanId(0, FrameFormat.Standard, FrameType.Error),
            ReadOnlyMemory<byte>.Empty,
            FrameFlags.ErrFrame,
            ChannelId.None,
            default);
        frame.IsError.Should().BeTrue();
    }

    [Fact]
    public void IsFd_False_For_Classical_Can()
    {
        var frame = new CanFrame(
            new CanId(0x100, FrameFormat.Standard),
            new byte[] { 0x01, 0x02 },
            FrameFlags.None,
            new ChannelId(7),
            Timestamp.FromMillis(123, 456));
        frame.IsFd.Should().BeFalse();
        frame.IsError.Should().BeFalse();
        frame.Dlc.Should().Be(2);
        frame.Channel.Handle.Should().Be(7);
        frame.Timestamp.TotalMicroseconds.Should().Be(123_456UL);
    }

    [Fact]
    public void Id_Roundtrips_Through_Positional_Property()
    {
        var id = new CanId(0x123, FrameFormat.Standard);
        new CanFrame(id, ReadOnlyMemory<byte>.Empty, FrameFlags.None,
            ChannelId.None, default).Id.Should().Be(id);
    }

    [Fact]
    public void Equal_Frames_With_Different_Array_Instances_Are_Equal()
    {
        // M8 regression: ReadOnlyMemory<byte>'s default equality compares
        // the underlying array reference, not the content. Two CanFrames
        // with identical byte content from different array instances
        // must be equal.
        var data1 = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var data2 = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var id = new CanId(0x100, FrameFormat.Standard);
        var ts = Timestamp.FromMicroseconds(12345);
        var frame1 = new CanFrame(id, data1, FrameFlags.None, ChannelId.None, ts);
        var frame2 = new CanFrame(id, data2, FrameFlags.None, ChannelId.None, ts);

        frame1.Should().Be(frame2, "content-equal frames must be structurally equal");
        (frame1 == frame2).Should().BeTrue();
        frame1.GetHashCode().Should().Be(frame2.GetHashCode());
    }

    [Fact]
    public void Different_Data_Makes_Frames_Not_Equal()
    {
        var id = new CanId(0x100, FrameFormat.Standard);
        var ts = Timestamp.FromMicroseconds(12345);
        var frame1 = new CanFrame(id, new byte[] { 0x01 }, FrameFlags.None, ChannelId.None, ts);
        var frame2 = new CanFrame(id, new byte[] { 0x02 }, FrameFlags.None, ChannelId.None, ts);

        frame1.Should().NotBe(frame2);
        (frame1 != frame2).Should().BeTrue();
    }
}