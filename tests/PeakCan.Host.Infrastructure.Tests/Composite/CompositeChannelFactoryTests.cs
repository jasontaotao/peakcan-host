using FluentAssertions;
using NSubstitute;
using PeakCan.HIL.Core;
using PeakCan.Host.Infrastructure.Composite;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests.Composite;

/// <summary>
/// Tests for <see cref="CompositeChannelFactory"/>.
/// </summary>
public sealed class CompositeChannelFactoryTests
{
    [Fact]
    public void Create_Routes_ToFirstFactory_ForPeakHandle()
    {
        // Arrange
        var f1 = Substitute.For<IChannelFactory>();
        var f2 = Substitute.For<IChannelFactory>();
        var composite = new CompositeChannelFactory(new[] { f1, f2 });

        var id = new ChannelId(0x51);

        // Act
        composite.Create(id);

        // Assert — first factory should be called for 0x51 handle
        f1.Received(1).Create(Arg.Is<ChannelId>(c => c.Handle == 0x51));
    }

    [Fact]
    public void Create_Routes_ToZlgFactory_ForZlgHandle()
    {
        // Arrange
        var f1 = Substitute.For<IChannelFactory>();
        var f2 = Substitute.For<IChannelFactory>();
        var composite = new CompositeChannelFactory(new[] { f1, f2 });

        var id = new ChannelId(0x8600);

        // Act
        composite.Create(id);

        // Assert — both factories get called, but the ZLG-range one returns the channel
        f1.Received(1).Create(Arg.Is<ChannelId>(c => c.Handle == 0x8600));
    }

    [Fact]
    public void Create_Returns_Channel_From_SubFactory()
    {
        // Arrange
        var channel = Substitute.For<ICanChannel>();
        var f1 = Substitute.For<IChannelFactory>();
        f1.Create(Arg.Any<ChannelId>()).Returns(channel);
        var composite = new CompositeChannelFactory(new[] { f1 });

        // Act
        var result = composite.Create(new ChannelId(0x51));

        // Assert
        result.Should().Be(channel);
    }

    [Fact]
    public void Constructor_Throws_OnNull()
    {
        var act = () => new CompositeChannelFactory(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Create_WithNoFactories_Throws()
    {
        var composite = new CompositeChannelFactory(Array.Empty<IChannelFactory>());
        var act = () => composite.Create(new ChannelId(0x51));
        act.Should().Throw<IndexOutOfRangeException>();
    }
}