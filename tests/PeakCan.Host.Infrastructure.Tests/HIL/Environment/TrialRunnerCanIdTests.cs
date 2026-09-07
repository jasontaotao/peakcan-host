using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Environment;
using PeakCan.Host.Infrastructure.HIL.Environment;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Environment;

public class TrialRunnerCanIdTests
{
    [Fact]
    public async Task StandardAndExtendedRaw_AreDistinct()
    {
        var channel = new FakeChannel();
        var runner = new TrialRunner(channel)
        {
            MessageIdLookup = name => name == "BRM_EXT"
                ? new CanId(0x100, FrameFormat.Extended)
                : null
        };
        _ = Task.Run(async () =>
        {
            await Task.Delay(25);
            channel.RaiseFrameReceived(new CanFrame(
                new CanId(0x100, FrameFormat.Standard), new byte[] { 1 }, FrameFlags.None, default, default));
        });

        var result = await runner.RunTrialAsync([MakeNode("BRM_EXT", 50)], CancellationToken.None);

        Assert.False(result.Diagnostics[0].Passed);
    }

    private static RestbusNode MakeNode(string thenReceive, int timeoutMs) => new()
    {
        Name = "T",
        Identity = new RawCanNodeIdentity(),
        Trial = new TrialContract("tpl",
            [new HandshakeExpectation("CRM", thenReceive, timeoutMs, ["cause"])], [])
    };
}
