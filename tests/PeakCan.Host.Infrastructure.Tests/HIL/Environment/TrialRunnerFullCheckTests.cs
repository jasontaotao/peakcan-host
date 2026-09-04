using Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Environment;
using PeakCan.Host.Infrastructure.HIL.Environment;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Environment;

public class TrialRunnerFullCheckTests
{
    [Fact]
    public async Task RunTrial_WithoutLookup_ReturnsPreviewMode()
    {
        var runner = new TrialRunner(new FakeChannel());
        var node = new RestbusNode
        {
            Name = "T", Identity = new RawCanNodeIdentity(),
            Trial = new TrialContract("tpl",
                [new HandshakeExpectation("CRM", "BRM", 500, ["cause"])], [])
        };
        var result = await runner.RunTrialAsync([node], TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.True(result.Passed);
        Assert.False(result.IsFullHandshakeCheck);
        Assert.Single(result.Diagnostics);
    }

    [Fact]
    public async Task RunTrial_WithLookup_FrameReceived_Passes()
    {
        var channel = new FakeChannel();
        var runner = new TrialRunner(channel) { MessageIdLookup = name => name == "BRM" ? 0x100 : null };

        // Emit BRM frame after a short delay
        _ = Task.Run(async () =>
        {
            await Task.Delay(50);
            channel.RaiseFrameReceived(new CanFrame(
                new CanId(0x100, FrameFormat.Standard), new byte[] { 1 }, FrameFlags.None, default, default));
        });

        var node = new RestbusNode
        {
            Name = "T", Identity = new RawCanNodeIdentity(),
            Trial = new TrialContract("tpl",
                [new HandshakeExpectation("CRM", "BRM", 500, ["cause"])], [])
        };
        var result = await runner.RunTrialAsync([node], TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.True(result.Passed);
        Assert.True(result.IsFullHandshakeCheck);
        Assert.True(result.Diagnostics[0].Passed);
    }

    [Fact]
    public async Task RunTrial_WithLookup_Timeout_Fails()
    {
        var channel = new FakeChannel();
        var runner = new TrialRunner(channel) { MessageIdLookup = name => name == "BRM" ? 0x100 : null };
        // No frame emitted → timeout

        var node = new RestbusNode
        {
            Name = "T", Identity = new RawCanNodeIdentity(),
            Trial = new TrialContract("tpl",
                [new HandshakeExpectation("CRM", "BRM", 100, ["接线/通道选错"])], [])
        };
        var result = await runner.RunTrialAsync([node], TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.False(result.Passed);
        Assert.False(result.Diagnostics[0].Passed);
        Assert.Contains("接线/通道选错", result.Diagnostics[0].PossibleCauses);
    }
}