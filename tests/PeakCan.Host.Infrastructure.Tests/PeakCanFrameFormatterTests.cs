using FluentAssertions;
using PeakCan.Host.Infrastructure.Channel;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests;

/// <summary>
/// Unit tests for the pure helpers in <see cref="PeakCanFrameFormatter"/>.
/// These run without PEAK hardware because the helpers are intentionally
/// side-effect-free.
/// </summary>
public class PeakCanFrameFormatterTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(8, 8)]
    [InlineData(9, 12)]
    [InlineData(10, 16)]
    [InlineData(11, 20)]
    [InlineData(12, 24)]
    [InlineData(13, 32)]
    [InlineData(14, 48)]
    [InlineData(15, 64)]
    [InlineData(0xFF, 64)]   // out-of-range saturates at 64
    public void DlcToBytes_Follows_Can_Fd_Sizing(byte dlc, byte expected)
    {
        PeakCanFrameFormatter.DlcToBytes(dlc).Should().Be(expected);
    }

    [Fact]
    public void ToFixedBytes8_Pads_Short_Source_With_Zeros()
    {
        var src = new byte[] { 0x11, 0x22, 0x33 };
        PeakCanFrameFormatter.ToFixedBytes8(src).Should().Equal(0x11, 0x22, 0x33, 0, 0, 0, 0, 0);
    }

    [Fact]
    public void ToFixedBytes8_Truncates_Overlong_Source()
    {
        var src = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99 };
        PeakCanFrameFormatter.ToFixedBytes8(src).Should().Equal(0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88);
    }

    [Fact]
    public void ToFixedBytes64_Pads_Short_Source_With_Zeros()
    {
        var src = new byte[] { 0xAA, 0xBB };
        var dst = PeakCanFrameFormatter.ToFixedBytes64(src);
        dst.Length.Should().Be(64);
        dst[0].Should().Be(0xAA);
        dst[1].Should().Be(0xBB);
        dst[2].Should().Be(0);
        dst[63].Should().Be(0);
    }

    [Fact]
    public void ToFixedBytes8_Empty_Source_Is_All_Zero()
    {
        PeakCanFrameFormatter.ToFixedBytes8(ReadOnlyMemory<byte>.Empty)
            .Should().Equal(Enumerable.Repeat<byte>(0, 8).ToArray());
    }

    [Fact]
    public void ToFixedBytes64_Empty_Source_Is_All_Zero()
    {
        var dst = PeakCanFrameFormatter.ToFixedBytes64(ReadOnlyMemory<byte>.Empty);
        dst.Length.Should().Be(64);
        dst.Should().AllBeEquivalentTo<byte>(0);
    }
}
