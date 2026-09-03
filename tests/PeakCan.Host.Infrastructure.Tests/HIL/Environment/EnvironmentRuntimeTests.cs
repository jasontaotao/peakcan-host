using Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Environment;
using PeakCan.Host.Infrastructure.HIL.Environment;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Environment;

public class EnvironmentRuntimeTests
{
    [Fact]
    public void Start_WithEmptyNodes_DoesNotThrow()
    {
        var runtime = new EnvironmentRuntime(new FakeChannel(), NullLogger<EnvironmentRuntime>.Instance);
        runtime.Start([], null);
        runtime.Stop();
    }

    [Fact]
    public void Start_EnabledMessage_SendsImmediately()
    {
        var sent = new List<CanFrame>();
        var channel = new FakeChannel { OnWrite = f => sent.Add(f) };
        var node = new RestbusNode
        {
            Name = "A",
            Identity = new RawCanNodeIdentity(),
            Messages = [new NodeMessage(new CanMessageRef(0x123, false), 100, new FixedHexSource("01 02"))],
        };
        var runtime = new EnvironmentRuntime(channel, NullLogger<EnvironmentRuntime>.Instance);
        runtime.Start([node], null);

        Assert.Single(sent);
        Assert.Equal(0x123u, sent[0].Id.Raw);
        Assert.Equal(FrameSource.Environment, sent[0].FrameSource);
        runtime.Stop();
    }

    [Fact]
    public void Start_DisabledMessage_DoesNotSend()
    {
        var sent = new List<CanFrame>();
        var channel = new FakeChannel { OnWrite = f => sent.Add(f) };
        var node = new RestbusNode
        {
            Name = "A",
            Identity = new RawCanNodeIdentity(),
            Messages = [new NodeMessage(new CanMessageRef(0x123, false), 100, new FixedHexSource("01 02"), Enabled: false)],
        };
        var runtime = new EnvironmentRuntime(channel, NullLogger<EnvironmentRuntime>.Instance);
        runtime.Start([node], null);
        Assert.Empty(sent);
        runtime.Stop();
    }

    [Fact]
    public void Stop_IsIdempotent()
    {
        var runtime = new EnvironmentRuntime(new FakeChannel(), NullLogger<EnvironmentRuntime>.Instance);
        runtime.Start([], null);
        runtime.Stop();
        runtime.Stop();
    }

    [Fact]
    public void UpdateFrameData_FixedHexSource_Applies()
    {
        var sent = new List<CanFrame>();
        var channel = new FakeChannel { OnWrite = f => sent.Add(f) };
        var node = new RestbusNode
        {
            Name = "A",
            Identity = new RawCanNodeIdentity(),
            Messages = [new NodeMessage(new CanMessageRef(0x123, false), 10, new FixedHexSource("AA BB"))],
        };
        var runtime = new EnvironmentRuntime(channel, NullLogger<EnvironmentRuntime>.Instance);
        runtime.Start([node], null);
        runtime.UpdateFrameData("A", new CanMessageRef(0x123, false), [0xCC, 0xDD]);
        // First frame from Start was "AA BB"; after update, next scan tick sends "CC DD"
        Thread.Sleep(100); // wait for at least one 10ms scan tick
        Assert.Contains(sent, f => f.Data.ToArray()[0] == 0xCC);
        runtime.Stop();
    }
}
