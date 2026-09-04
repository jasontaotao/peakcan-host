using Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Environment;
using PeakCan.Host.Infrastructure.HIL.Environment;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Environment;

public class NodeRunStatsTests
{
    [Fact]
    public void GetStats_ReturnsFrameCounts()
    {
        var node = new RestbusNode
        {
            Name = "Charger",
            Identity = new RawCanNodeIdentity(),
            Messages = [new NodeMessage(new CanMessageRef(0x100, false), 100, new FixedHexSource("0102"))]
        };
        var runtime = new EnvironmentRuntime(new FakeChannel(), NullLogger<EnvironmentRuntime>.Instance);
        runtime.Start([node], null);
        System.Threading.Thread.Sleep(150);
        var stats = runtime.GetStats();
        var chargerStats = Assert.Single(stats);
        Assert.Equal("Charger", chargerStats.NodeName);
        Assert.True(chargerStats.FramesSent > 0, $"Expected FramesSent > 0 but got {chargerStats.FramesSent}");
        runtime.Stop();
    }

    [Fact]
    public void GetStats_EmptyNodes_ReturnsEmpty()
    {
        var runtime = new EnvironmentRuntime(new FakeChannel(), NullLogger<EnvironmentRuntime>.Instance);
        runtime.Start([], null);
        Assert.Empty(runtime.GetStats());
        runtime.Stop();
    }
}