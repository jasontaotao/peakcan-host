using FluentAssertions;
using NSubstitute;
using PeakCan.HIL.Core;
using PeakCan.Host.Infrastructure.Composite;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests.Composite;

/// <summary>
/// Tests for <see cref="CompositeChannelProbe"/>.
/// </summary>
public sealed class CompositeChannelProbeTests
{
    [Fact]
    public void Probe_Returns_FirstSuccess()
    {
        // Arrange
        var p1 = Substitute.For<IChannelProbe>();
        p1.Probe(0x51).Returns(new ProbeResult(false, "fail"));
        var p2 = Substitute.For<IChannelProbe>();
        p2.Probe(0x51).Returns(new ProbeResult(true, "ok"));
        var p3 = Substitute.For<IChannelProbe>();
        p3.Probe(0x51).Returns(new ProbeResult(true, "also ok"));

        var composite = new CompositeChannelProbe(new[] { p1, p2, p3 });

        // Act
        var result = composite.Probe(0x51);

        // Assert
        result.Ok.Should().BeTrue();
        result.Message.Should().Be("ok");
        // p3 should not be called because p2 already succeeded
        p3.DidNotReceive().Probe(0x51);
    }

    [Fact]
    public void Probe_AllFail_Returns_LastFailure()
    {
        // Arrange
        var p1 = Substitute.For<IChannelProbe>();
        p1.Probe(0x51).Returns(new ProbeResult(false, "fail1"));
        var p2 = Substitute.For<IChannelProbe>();
        p2.Probe(0x51).Returns(new ProbeResult(false, "fail2"));

        var composite = new CompositeChannelProbe(new[] { p1, p2 });

        // Act
        var result = composite.Probe(0x51);

        // Assert
        result.Ok.Should().BeFalse();
        result.Message.Should().Be("fail2");
    }

    [Fact]
    public void Probe_NoProbes_Returns_DefaultFailure()
    {
        // Arrange
        var composite = new CompositeChannelProbe(Array.Empty<IChannelProbe>());

        // Act
        var result = composite.Probe(0x51);

        // Assert
        result.Ok.Should().BeFalse();
        result.Message.Should().Be("No probe available");
    }

    [Fact]
    public void Constructor_Throws_OnNull()
    {
        var act = () => new CompositeChannelProbe(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Probe_Skips_SelfType()
    {
        // Arrange — composite should skip itself to avoid recursion
        var inner = Substitute.For<IChannelProbe>();
        inner.Probe(0x51).Returns(new ProbeResult(true, "ok"));

        var self = new CompositeChannelProbe(new[] { inner });
        // The self-probe returns failure, but inner should still be called
        var outer = new CompositeChannelProbe(new[] { self, inner });

        // Act
        var result = outer.Probe(0x51);

        // Assert
        result.Ok.Should().BeTrue();
        // inner.Probe should be called (and found)
        inner.Received(1).Probe(0x51);
    }
}