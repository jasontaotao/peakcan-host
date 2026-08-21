using FluentAssertions;
using NSubstitute;
using PeakCan.HIL.Core;
using PeakCan.Host.Infrastructure.Composite;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests.Composite;

/// <summary>
/// Tests for <see cref="CompositeChannelEnumerator"/>.
/// </summary>
public sealed class CompositeChannelEnumeratorTests
{
    [Fact]
    public void Enumerate_Aggregates_AllEnumerators()
    {
        // Arrange
        var e1 = Substitute.For<IChannelEnumerator>();
        e1.Enumerate().Returns(new[] { new ChannelInfo(0x51, "PEAK-0"), new ChannelInfo(0x52, "PEAK-1") });
        var e2 = Substitute.For<IChannelEnumerator>();
        e2.Enumerate().Returns(new[] { new ChannelInfo(0x8600, "ZLG-0"), new ChannelInfo(0x8601, "ZLG-1") });

        var composite = new CompositeChannelEnumerator(new[] { e1, e2 });

        // Act
        var result = composite.Enumerate();

        // Assert
        result.Should().HaveCount(4);
        result.Select(r => r.Handle).Should().Contain(new ushort[] { 0x51, 0x52, 0x8600, 0x8601 });
    }

    [Fact]
    public void Enumerate_Deduplicates_ByHandle()
    {
        // Arrange
        var e1 = Substitute.For<IChannelEnumerator>();
        e1.Enumerate().Returns(new[] { new ChannelInfo(0x51, "PEAK-0"), new ChannelInfo(0x52, "PEAK-1") });
        var e2 = Substitute.For<IChannelEnumerator>();
        e2.Enumerate().Returns(new[] { new ChannelInfo(0x51, "PEAK-0-duplicate"), new ChannelInfo(0x8600, "ZLG-0") });

        var composite = new CompositeChannelEnumerator(new[] { e1, e2 });

        // Act
        var result = composite.Enumerate();

        // Assert — 0x51 appears only once (first wins)
        result.Should().HaveCount(3);
        result.Should().ContainSingle(c => c.Handle == 0x51);
        result.First(c => c.Handle == 0x51).Name.Should().Be("PEAK-0");
    }

    [Fact]
    public void Enumerate_Empty_Returns_Empty()
    {
        // Arrange
        var composite = new CompositeChannelEnumerator(Array.Empty<IChannelEnumerator>());

        // Act
        var result = composite.Enumerate();

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void Enumerate_AllEnumeratorsEmpty_Returns_Empty()
    {
        // Arrange
        var e1 = Substitute.For<IChannelEnumerator>();
        e1.Enumerate().Returns(Array.Empty<ChannelInfo>());
        var e2 = Substitute.For<IChannelEnumerator>();
        e2.Enumerate().Returns(Array.Empty<ChannelInfo>());

        var composite = new CompositeChannelEnumerator(new[] { e1, e2 });

        // Act
        var result = composite.Enumerate();

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void Enumerate_Skips_SelfType()
    {
        // Arrange — composite should skip itself to avoid recursion
        var inner = Substitute.For<IChannelEnumerator>();
        inner.Enumerate().Returns(new[] { new ChannelInfo(0x51, "PEAK-0") });

        var self = new CompositeChannelEnumerator(new[] { inner });
        var outer = new CompositeChannelEnumerator(new[] { self, inner });

        // Act
        var result = outer.Enumerate();

        // Assert — only one copy of 0x51
        result.Should().HaveCount(1);
    }

    [Fact]
    public void Constructor_Throws_OnNull()
    {
        var act = () => new CompositeChannelEnumerator(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}