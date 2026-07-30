using PeakCan.Host.Core;
using PeakCan.Host.Infrastructure.CanChannels;

namespace PeakCan.Host.Infrastructure.Tests.Channel;

public class VirtualChannelTests
{
    [Fact]
    public async Task ConnectAsync_sets_IsConnected_true()
    {
        await using var channel = new VirtualChannel();
        await channel.ConnectAsync(BaudRate.Can500kbps, false);
        Assert.True(channel.IsConnected);
    }

    [Fact]
    public async Task WriteAsync_loops_back_to_FrameReceived()
    {
        var channel = new VirtualChannel();
        var tcs = new TaskCompletionSource<CanFrame>();
        channel.FrameReceived += f => tcs.TrySetResult(f);

        await channel.ConnectAsync(BaudRate.Can500kbps, false);
        var frame = new CanFrame(new CanId(0x123, FrameFormat.Standard), new ReadOnlyMemory<byte>(new byte[] { 1, 2, 3 }), FrameFlags.None, ChannelId.None, new Timestamp(0));

        var writeResult = await channel.WriteAsync(frame);
        Assert.True(writeResult.IsSuccess);

        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(frame.Id, received.Id);
        Assert.Equal(frame.Data.ToArray(), received.Data.ToArray());

        await channel.DisposeAsync();
    }

    [Fact]
    public async Task FrameReceived_supports_multiple_subscribers()
    {
        var channel = new VirtualChannel();
        var tcs1 = new TaskCompletionSource<CanFrame>();
        var tcs2 = new TaskCompletionSource<CanFrame>();
        channel.FrameReceived += f => tcs1.TrySetResult(f);
        channel.FrameReceived += f => tcs2.TrySetResult(f);

        await channel.ConnectAsync(BaudRate.Can500kbps, false);
        var frame = new CanFrame(new CanId(0x456, FrameFormat.Standard), new ReadOnlyMemory<byte>(new byte[] { 9 }), FrameFlags.None, ChannelId.None, new Timestamp(0));

        await channel.WriteAsync(frame);

        var r1 = await tcs1.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var r2 = await tcs2.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0x456u, r1.Id.Raw);
        Assert.Equal(0x456u, r2.Id.Raw);

        await channel.DisposeAsync();
    }

    [Fact]
    public async Task DropOldest_keeps_latest_when_full()
    {
        var channel = new VirtualChannel(capacity: 2);
        var received = new List<CanFrame>();
        var gate = new TaskCompletionSource();
        var count = 0;
        channel.FrameReceived += f =>
        {
            lock (received)
            {
                received.Add(f);
                if (++count == 2) gate.TrySetResult();
            }
        };

        await channel.ConnectAsync(BaudRate.Can500kbps, false);

        // Write 3 frames rapidly (faster than consumer can process)
        for (int i = 0; i < 3; i++)
        {
            var frame = new CanFrame(new CanId((uint)(0x100 + i), FrameFormat.Standard), new ReadOnlyMemory<byte>(new byte[] { (byte)i }), FrameFlags.None, ChannelId.None, new Timestamp(0));
            await channel.WriteAsync(frame);
        }

        await gate.Task.WaitAsync(TimeSpan.FromSeconds(3));

        lock (received)
        {
            // DropOldest: oldest (0x100) dropped, keep 0x101 and 0x102
            Assert.Equal(2, received.Count);
            Assert.Equal(0x101u, received[0].Id.Raw);
            Assert.Equal(0x102u, received[1].Id.Raw);
        }

        await channel.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_is_idempotent()
    {
        var channel = new VirtualChannel();
        await channel.ConnectAsync(BaudRate.Can500kbps, false);
        await channel.DisposeAsync();
        await channel.DisposeAsync(); // should not throw
    }

    [Fact]
    public void Implements_ICanChannel_all_members()
    {
        var channel = new VirtualChannel();
        // Id property
        Assert.Equal(ChannelId.None, channel.Id);

        // ReadLoopError add/remove should not throw
        var handler = new Action<ReadLoopError>(_ => { });
        channel.ReadLoopError += handler;
        channel.ReadLoopError -= handler;
    }
}
