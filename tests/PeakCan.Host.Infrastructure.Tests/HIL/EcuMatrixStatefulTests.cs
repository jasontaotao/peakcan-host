using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.Uds.IsoTp;
using PeakCan.Host.Infrastructure.HIL;

namespace PeakCan.Host.Infrastructure.Tests.HIL;

public class EcuMatrixStatefulTests
{
    private static EcuScript CreateScript(string name, uint ecuSendCanId, EcuStateMachine sm)
    {
        // EcuScript.CanIds: RequestId = ECU send CAN ID, ResponseId = HIL send CAN ID
        return new EcuScript(name, new CanIdConfig
        {
            RequestId = ecuSendCanId,   // ECU sends here (HIL listens)
            ResponseId = 0x7E0,         // HIL sends here (ECU listens)
        }, sm);
    }

    [Fact]
    public async Task EcuMatrix_AddEcu_Stateful_RespondsToRequest()
    {
        using var matrix = new EcuMatrix();
        var sm = new EcuStateMachine(new[]
        {
            new EcuStateTransition
            {
                FromState = null,
                ServiceId = 0x3E,
                SubFunction = 0x00,
                Response = new StaticResponse(new byte[] { 0x7E }),
            }
        });
        var script = CreateScript("BMS", 0x7E8, sm);
        matrix.AddEcu(script);

        var tcs = new TaskCompletionSource<CanFrame>();
        matrix.Channel.FrameReceived += f =>
        {
            if (f.Id.Raw == 0x7E8) tcs.TrySetResult(f);
        };

        await matrix.ConnectAsync(BaudRate.Can500kbps, false);

        // Send UDS TesterPresent to 0x7E0
        var requestFrame = new CanFrame(
            new CanId(0x7E0, FrameFormat.Standard),
            new ReadOnlyMemory<byte>(new byte[] { 0x02, 0x3E, 0x00 }),
            FrameFlags.None, ChannelId.None, new Timestamp(0));

        await matrix.Channel.WriteAsync(requestFrame);

        var response = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0x7E8u, response.Id.Raw);
    }

    [Fact]
    public void EcuMatrix_AddEcu_DetectsCanIdConflict()
    {
        using var matrix = new EcuMatrix();
        var sm = new EcuStateMachine(new[]
        {
            new EcuStateTransition
            {
                FromState = null,
                ServiceId = 0x3E,
                Response = new StaticResponse(new byte[] { 0x7E }),
            }
        });
        var script1 = CreateScript("BMS", 0x7E8, sm);
        var script2 = CreateScript("MCU", 0x7E8, sm); // Same send CAN ID

        matrix.AddEcu(script1);
        Assert.Throws<InvalidOperationException>(() => matrix.AddEcu(script2));
    }
}
