using FluentAssertions;
using PeakCan.Host.Core;
using Xunit;

namespace PeakCan.Host.Core.Tests;

public class CanIdTests
{
    [Fact]
    public void Standard_Accepts_11Bit_Id()
    {
        var id = new CanId(0x123, FrameFormat.Standard);
        id.Raw.Should().Be(0x123);
        id.IsExtended.Should().BeFalse();
    }

    [Fact]
    public void Standard_Rejects_29Bit_Id()
    {
        Action act = () => new CanId(0x800, FrameFormat.Standard);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Extended_Accepts_29Bit_Id()
    {
        var id = new CanId(0x18FF1234, FrameFormat.Extended);
        id.IsExtended.Should().BeTrue();
    }

    [Fact]
    public void Extended_Rejects_Over_29Bit()
    {
        Action act = () => new CanId(0x40000000, FrameFormat.Extended);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0x000)]
    [InlineData(0x7FF)]
    public void Standard_Boundaries_Are_Valid(uint raw) =>
        new CanId(raw, FrameFormat.Standard).Raw.Should().Be(raw);

    [Theory]
    [InlineData(0x000)]
    [InlineData(0x1FFFFFFF)]
    public void Extended_Boundaries_Are_Valid(uint raw) =>
        new CanId(raw, FrameFormat.Extended).Raw.Should().Be(raw);

    [Fact]
    public void ToString_Formats_Standard_As_3Hex()
        => new CanId(0x123, FrameFormat.Standard).ToString().Should().Be("0x123");

    [Fact]
    public void ToString_Formats_Extended_As_8Hex()
        => new CanId(0x18FF1234, FrameFormat.Extended).ToString().Should().Be("0x18FF1234");

    [Fact]
    public void Type_Can_Be_Overridden_Via_With()
    {
        var id = new CanId(1, FrameFormat.Standard) with { Type = FrameType.Remote };
        id.Type.Should().Be(FrameType.Remote);
    }
}