using System.Diagnostics;
using PeakCan.Host.Core;
using PeakCan.Host.Infrastructure.Channel;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests;

public class TraceDrivenChannelLoopbackTests
{
    [Fact]
    public async Task WriteAsync_FrameReceived_Raised()
    {
        // Arrange
        var channel = new TraceDrivenChannel(new ChannelId(1));
        var receivedFrames = new List<CanFrame>();
        channel.FrameReceived += f => receivedFrames.Add(f);
        var frame = new CanFrame(new CanId(0x123, FrameFormat.Standard),
            new byte[] { 0xDE, 0xAD }, FrameFlags.None, default, default);

        // Act
        await channel.WriteAsync(frame);

        // Assert
        Assert.Single(receivedFrames);
        Assert.Equal(0x123u, receivedFrames[0].Id.Raw);
        Assert.Equal(new byte[] { 0xDE, 0xAD }, receivedFrames[0].Data.ToArray());
    }

    [Fact]
    public async Task WriteAsync_StimulusResponse_TraceNotLoaded()
    {
        // Arrange - no trace loaded
        var channel = new TraceDrivenChannel(new ChannelId(1));
        var receivedFrames = new List<CanFrame>();
        channel.FrameReceived += f => receivedFrames.Add(f);

        // Act - write multiple frames without loading trace
        for (int i = 0; i < 5; i++)
        {
            await channel.WriteAsync(new CanFrame(new CanId(0x100 + (uint)i, FrameFormat.Standard),
                new byte[] { (byte)i }, FrameFlags.None, default, default));
        }

        // Assert
        Assert.Equal(5, receivedFrames.Count);
        Assert.Equal(0x100u, receivedFrames[0].Id.Raw);
        Assert.Equal(0x104u, receivedFrames[4].Id.Raw);
    }

    [Fact]
    public async Task WriteAsync_DropOldest_OverflowDoesNotBlock()
    {
        // Arrange - channel capacity = 1000
        var channel = new TraceDrivenChannel(new ChannelId(1));
        var receivedCount = 0;
        channel.FrameReceived += f => Interlocked.Increment(ref receivedCount);

        // Act - write 1001 frames (exceeds capacity by 1)
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 1001; i++)
        {
            await channel.WriteAsync(new CanFrame(new CanId(0x200, FrameFormat.Standard),
                new byte[] { (byte)(i % 256) }, FrameFlags.None, default, default));
        }
        sw.Stop();

        // Assert - should not block, all frames emitted
        Assert.True(sw.ElapsedMilliseconds < 1000, $"WriteAsync blocked for {sw.ElapsedMilliseconds}ms");
        Assert.Equal(1001, receivedCount);
    }
}
