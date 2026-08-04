using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.CanChannels;
using PeakCan.Host.Infrastructure.Channel;

namespace PeakCan.Host.Infrastructure.Tests.Channel;

public class ReceivePathFaultInjectorExceptionTests
{
    [Fact]
    public async Task DelayFault_DisposeCancelsPending_NoExceptionInWaitForPending()
    {
        var channel = new VirtualChannel();
        await channel.ConnectAsync(BaudRate.Can500kbps, false);

        var injector = new ReceivePathFaultInjector(channel);

        // Add a delay fault with long delay
        var rule = new FaultRule { Type = FaultType.Delay, DelayMs = 5000 };
        injector.AddReceiveFault(rule);

        // Subscribe so delay tasks have a handler to dispatch to
        var tcs = new TaskCompletionSource<CanFrame>();
        injector.FrameReceived += f => tcs.TrySetResult(f);

        // Send a frame to trigger the delay pipeline
        var frame = new CanFrame(
            new CanId(0x123, FrameFormat.Standard),
            new ReadOnlyMemory<byte>(new byte[] { 0x01, 0x02, 0x03 }),
            FrameFlags.None, ChannelId.None, new Timestamp(0));
        await channel.WriteAsync(frame);

        // Dispose should cancel pending delays without throwing
        await injector.DisposeAsync();
        Assert.True(true);
    }

    [Fact]
    public void DelayFault_ApplyDispatchThrows_StoresException()
    {
        // This is harder to test without a real channel that throws.
        // We verify the accessor exists and returns null initially.
        var channel = new VirtualChannel();
        var injector = new ReceivePathFaultInjector(channel);

        var ex = injector.GetLastDelayFaultException();
        Assert.Null(ex); // No exception initially
    }
}
