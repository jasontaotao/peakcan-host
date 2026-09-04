using Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Environment;
using PeakCan.Host.Infrastructure.HIL.Environment;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Environment;

public class EnvironmentRuleDispatchTests
{
    private static CanFrame MakeFrame(uint id, byte[] data, FrameSource source = FrameSource.Bus) =>
        new(new CanId(id, FrameFormat.Standard), data, FrameFlags.None, default, default, source);

    [Fact]
    public void IncomingFrame_MatchesRule_SendsResponse()
    {
        var sent = new List<CanFrame>();
        var channel = new FakeChannel { OnWrite = f => sent.Add(f) };
        var node = new RestbusNode
        {
            Name = "A",
            Identity = new RawCanNodeIdentity(),
            Rules =
            [
                new ResponseRule(
                    new CanMessageRef(0x500, false), null,
                    new SendMessageAction(new CanMessageRef(0x600, false), new FixedHexSource("01")),
                    0),
            ],
        };
        var runtime = new EnvironmentRuntime(channel, NullLogger<EnvironmentRuntime>.Instance);
        runtime.Start([node], null);
        runtime.InjectIncomingFrame(MakeFrame(0x500, [0x01]));
        runtime.ScanForTest();
        Assert.Contains(sent, f => f.Id.Raw == 0x600);
        runtime.Stop();
    }

    [Fact]
    public void IncomingFrame_NoMatch_NoResponse()
    {
        var sent = new List<CanFrame>();
        var channel = new FakeChannel { OnWrite = f => sent.Add(f) };
        var node = new RestbusNode
        {
            Name = "A",
            Identity = new RawCanNodeIdentity(),
            Rules =
            [
                new ResponseRule(
                    new CanMessageRef(0x500, false), null,
                    new SendMessageAction(new CanMessageRef(0x600, false), new FixedHexSource("01")),
                    0),
            ],
        };
        var runtime = new EnvironmentRuntime(channel, NullLogger<EnvironmentRuntime>.Instance);
        runtime.Start([node], null);
        runtime.InjectIncomingFrame(MakeFrame(0x3FF, [0x01]));
        runtime.ScanForTest();
        Assert.DoesNotContain(sent, f => f.Id.Raw == 0x600);
        runtime.Stop();
    }

    [Fact]
    public void EnvironmentSourceFrame_Ignored()
    {
        var sent = new List<CanFrame>();
        var channel = new FakeChannel { OnWrite = f => sent.Add(f) };
        var node = new RestbusNode
        {
            Name = "A",
            Identity = new RawCanNodeIdentity(),
            Rules =
            [
                new ResponseRule(
                    new CanMessageRef(0x500, false), null,
                    new SendMessageAction(new CanMessageRef(0x600, false), new FixedHexSource("01")),
                    0),
            ],
        };
        var runtime = new EnvironmentRuntime(channel, NullLogger<EnvironmentRuntime>.Instance);
        runtime.Start([node], null);
        runtime.InjectIncomingFrame(MakeFrame(0x500, [0x01], source: FrameSource.Environment));
        runtime.ScanForTest();
        Assert.DoesNotContain(sent, f => f.Id.Raw == 0x600);
        runtime.Stop();
    }

    [Fact]
    public void BytePatternCondition_MatchingPayload_Triggers()
    {
        var sent = new List<CanFrame>();
        var channel = new FakeChannel { OnWrite = f => sent.Add(f) };
        var node = new RestbusNode
        {
            Name = "A",
            Identity = new RawCanNodeIdentity(),
            Rules =
            [
                new ResponseRule(
                    new CanMessageRef(0x500, false),
                    new BytePattern(0, 0xFF, 0x42),
                    new SendMessageAction(new CanMessageRef(0x600, false), new FixedHexSource("01")),
                    0),
            ],
        };
        var runtime = new EnvironmentRuntime(channel, NullLogger<EnvironmentRuntime>.Instance);
        runtime.Start([node], null);
        runtime.InjectIncomingFrame(MakeFrame(0x500, [0x42]));
        runtime.ScanForTest();
        Assert.Contains(sent, f => f.Id.Raw == 0x600);
        runtime.Stop();
    }

    [Fact]
    public void BytePatternCondition_NonMatchingPayload_DoesNotTrigger()
    {
        var sent = new List<CanFrame>();
        var channel = new FakeChannel { OnWrite = f => sent.Add(f) };
        var node = new RestbusNode
        {
            Name = "A",
            Identity = new RawCanNodeIdentity(),
            Rules =
            [
                new ResponseRule(
                    new CanMessageRef(0x500, false),
                    new BytePattern(0, 0xFF, 0x42),
                    new SendMessageAction(new CanMessageRef(0x600, false), new FixedHexSource("01")),
                    0),
            ],
        };
        var runtime = new EnvironmentRuntime(channel, NullLogger<EnvironmentRuntime>.Instance);
        runtime.Start([node], null);
        runtime.InjectIncomingFrame(MakeFrame(0x500, [0x99]));
        runtime.ScanForTest();
        Assert.DoesNotContain(sent, f => f.Id.Raw == 0x600);
        runtime.Stop();
    }
}
