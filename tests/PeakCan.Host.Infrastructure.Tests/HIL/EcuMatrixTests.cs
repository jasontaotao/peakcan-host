using PeakCan.Host.Core;
using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Core.Uds.IsoTp;
using PeakCan.Host.Infrastructure.HIL;

namespace PeakCan.Host.Infrastructure.Tests.HIL;

public class EcuMatrixTests
{
    private static EcuScript CreateScript(string name, uint requestId, uint responseId, params UdsResponseRule[] rules)
    {
        var sm = EcuStateMachine.FromRules(rules);
        return new EcuScript(name, new CanIdConfig { RequestId = requestId, ResponseId = responseId }, sm);
    }

    [Fact]
    public void AddEcu_creates_VirtualEcu_on_shared_channel()
    {
        using var matrix = new EcuMatrix();
        var script1 = CreateScript("BMS", 0x7E8, 0x7E0, new UdsResponseRule { ServiceId = 0x3E, ResponseData = new byte[] { 0x7E } });
        var script2 = CreateScript("MCU", 0x7EA, 0x7E2, new UdsResponseRule { ServiceId = 0x3E, ResponseData = new byte[] { 0x7E } });

        matrix.AddEcu(script1);
        matrix.AddEcu(script2);

        Assert.IsAssignableFrom<ICanChannel>(matrix.Channel);
    }

    [Fact]
    public void AddEcu_throws_on_CAN_ID_conflict()
    {
        using var matrix = new EcuMatrix();
        // ECU perspective: RequestId = ECU send ID, ResponseId = ECU receive ID
        // Two ECUs with same RequestId (send ID) = conflict
        var script1 = CreateScript("BMS", 0x7E8, 0x7E0);
        var script2 = CreateScript("MCU", 0x7E8, 0x7E2); // Same send ID (0x7E8) = conflict

        matrix.AddEcu(script1);

        // script2's VirtualEcu.RequestId = script2.CanIds.ResponseId = 0x7E2
        // script1's VirtualEcu.RequestId = script1.CanIds.ResponseId = 0x7E0
        // These are different, so no conflict. Use same ResponseId to create conflict:
        var script3 = CreateScript("VGM", 0x7E8, 0x7E0); // Same send ID as script1 (0x7E0)

        var ex = Assert.Throws<InvalidOperationException>(() => matrix.AddEcu(script3));
        Assert.Contains("CAN ID conflict", ex.Message);
    }

    [Fact]
    public async Task Channel_exposed_for_external_use()
    {
        using var matrix = new EcuMatrix();
        var channel = matrix.Channel;

        var received = new List<CanFrame>();
        channel.FrameReceived += f => received.Add(f);

        await channel.ConnectAsync(BaudRate.Can500kbps, false);

        var frame = new CanFrame(new CanId(0x100, FrameFormat.Standard), new ReadOnlyMemory<byte>(new byte[] { 1 }), FrameFlags.None, ChannelId.None, new Timestamp(0));
        await channel.WriteAsync(frame);

        await Task.Delay(100);
        Assert.Single(received);
    }

    [Fact]
    public async Task Dispose_disposes_all_ECUs_and_channel()
    {
        var matrix = new EcuMatrix();
        var script = CreateScript("BMS", 0x7E8, 0x7E0);
        matrix.AddEcu(script);

        var channel = matrix.Channel;
        await channel.ConnectAsync(BaudRate.Can500kbps, false);
        matrix.Dispose();

        // After dispose, WriteAsync should fail (channel closed)
        var frame = new CanFrame(new CanId(0x100, FrameFormat.Standard), new ReadOnlyMemory<byte>(), FrameFlags.None, ChannelId.None, new Timestamp(0));
        var result = await channel.WriteAsync(frame);
        Assert.False(result.IsSuccess);
    }
}
