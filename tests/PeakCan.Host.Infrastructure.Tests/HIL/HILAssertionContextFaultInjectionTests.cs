using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.CanChannels;
using PeakCan.Host.Infrastructure.HIL;

namespace PeakCan.Host.Infrastructure.Tests.HIL;

public class HILAssertionContextFaultInjectionTests
{
    [Fact]
    public async Task SendFrameAsync_goes_through_FaultInjector_when_enabled()
    {
        var channel = new VirtualChannel();
        var ctx = new HILAssertionContext(channel, new FakeDbcLookup(), enableFaultInjection: true);

        // Add a Drop fault for CAN ID 0x123
        ctx.AddFault(new FaultRule { Type = FaultType.Drop, TargetCanId = 0x123 });

        var received = new List<CanFrame>();
        channel.FrameReceived += f => received.Add(f);

        await channel.ConnectAsync(BaudRate.Can500kbps, false);

        var frame = new CanFrame(new CanId(0x123, FrameFormat.Standard), new ReadOnlyMemory<byte>(new byte[] { 1 }), FrameFlags.None, ChannelId.None, new Timestamp(0));
        var result = await ctx.SendFrameAsync(frame);

        Assert.True(result.IsSuccess);
        await Task.Delay(100);
        Assert.Empty(received); // Frame was dropped by FaultInjector

        await channel.DisposeAsync();
    }

    [Fact]
    public async Task SendFrameAsync_bypasses_FaultInjector_when_disabled()
    {
        var channel = new VirtualChannel();
        var ctx = new HILAssertionContext(channel, new FakeDbcLookup(), enableFaultInjection: false);

        var received = new List<CanFrame>();
        channel.FrameReceived += f => received.Add(f);

        await channel.ConnectAsync(BaudRate.Can500kbps, false);

        var frame = new CanFrame(new CanId(0x123, FrameFormat.Standard), new ReadOnlyMemory<byte>(new byte[] { 1 }), FrameFlags.None, ChannelId.None, new Timestamp(0));
        var result = await ctx.SendFrameAsync(frame);

        Assert.True(result.IsSuccess);
        await Task.Delay(100);
        Assert.Single(received); // Frame passed through

        await channel.DisposeAsync();
    }

    /// <summary>Minimal IDbcLookup stub for testing.</summary>
    private sealed class FakeDbcLookup : IDbcLookup
    {
        public PeakCan.HIL.Core.Dbc.Message? FindMessage(uint canId) => null;
    }
}
