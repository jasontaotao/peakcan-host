using PeakCan.Host.Core;
using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Core.Uds.IsoTp;
using PeakCan.Host.Infrastructure.CanChannels;
using PeakCan.Host.Infrastructure.Channel;
using PeakCan.Host.Infrastructure.HIL;

namespace PeakCan.Host.Infrastructure.Tests.HIL;

public class FaultInjectionE2ETests
{
    [Fact]
    public async Task FaultInjection_ReceiveDirection_DropsEcuResponse()
    {
        // Arrange: VirtualChannel + ReceivePathFaultInjector
        // No ECU needed — we test the injector's drop behavior directly
        // by simulating what happens when an ECU response frame loops back.
        var channel = new VirtualChannel();
        var rxInjector = new ReceivePathFaultInjector(channel);

        // Subscribe BEFORE adding fault to ensure subscription is active
        int receivedCount = 0;
        rxInjector.FrameReceived += _ => Interlocked.Increment(ref receivedCount);

        // Add a Drop fault on CAN ID 0x7E8 (simulated ECU response)
        rxInjector.AddReceiveFault(new FaultRule
        {
            Type = FaultType.Drop,
            TargetCanId = 0x7E8,
            Probability = 1.0,
        });

        await channel.ConnectAsync(BaudRate.Can500kbps, false);

        // Send a frame with CAN ID 0x7E8 (simulated ECU response) through the injector
        var responseFrame = new CanFrame(
            new CanId(0x7E8, FrameFormat.Standard),
            new ReadOnlyMemory<byte>(new byte[] { 0x02, 0x7E }),
            FrameFlags.None, ChannelId.None, new Timestamp(0));

        await rxInjector.WriteAsync(responseFrame);

        // Wait for any frames to be dispatched
        await Task.Delay(300);

        // Assert: frame should be dropped — subscriber should NOT receive it
        Assert.Equal(0, receivedCount);

        await channel.DisposeAsync();
    }
}
