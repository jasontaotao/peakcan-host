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
}