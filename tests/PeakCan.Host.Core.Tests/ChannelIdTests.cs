using FluentAssertions;
using PeakCan.Host.Core;
using Xunit;

namespace PeakCan.Host.Core.Tests;

public class ChannelIdTests
{
    [Fact]
    public void None_Has_Zero_Handle()
        => ChannelId.None.Handle.Should().Be((ushort)0);

    [Fact]
    public void ToString_Formats_Ch_Prefix()
        => new ChannelId(7).ToString().Should().Be("ch7");
}