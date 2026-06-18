using FluentAssertions;
using PeakCan.Host.Core;
using Xunit;

namespace PeakCan.Host.Core.Tests;

public class FrameFlagsTests
{
    [Fact]
    public void Can_Combine_Multiple_Flags()
    {
        var f = FrameFlags.Fd | FrameFlags.BitRateSwitch;
        f.HasFlag(FrameFlags.Fd).Should().BeTrue();
        f.HasFlag(FrameFlags.BitRateSwitch).Should().BeTrue();
        f.HasFlag(FrameFlags.Rtr).Should().BeFalse();
    }

    [Fact]
    public void None_Has_No_Flag()
    {
        FrameFlags.None.HasFlag(FrameFlags.Fd).Should().BeFalse();
        FrameFlags.None.HasFlag(FrameFlags.Rtr).Should().BeFalse();
        FrameFlags.None.HasFlag(FrameFlags.ErrFrame).Should().BeFalse();
    }
}