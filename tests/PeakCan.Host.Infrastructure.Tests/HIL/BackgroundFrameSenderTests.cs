using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.Host.Infrastructure.CanChannels;
using PeakCan.Host.Infrastructure.HIL;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests.HIL;

public class BackgroundFrameSenderTests
{
    private static BackgroundFrame MakeFrame(uint raw = 0x100, int periodMs = 50, byte[]? data = null) =>
        new(new CanId(raw, FrameFormat.Standard), data ?? new byte[] { 1, 2 }, periodMs, false);

    [Fact]
    public void Start_WithEmptyList_DoesNothing()
    {
        var channel = new VirtualChannel();
        var sender = new BackgroundFrameSender(channel, NullLogger.Instance);

        sender.Start(Array.Empty<BackgroundFrame>());

        // 不抛异常即可
        sender.Stop();
        sender.Dispose();
    }

    [Fact]
    public void Start_WithDuplicateId_Throws()
    {
        var channel = new VirtualChannel();
        var sender = new BackgroundFrameSender(channel, NullLogger.Instance);
        var frames = new[] { MakeFrame(0x100), MakeFrame(0x100) };

        var act = () => sender.Start(frames);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Duplicate background frame CAN ID*");
    }

    [Fact]
    public async Task Start_WithVirtualChannel_FramesAreSent()
    {
        var channel = new VirtualChannel();
        await channel.ConnectAsync(BaudRate.CanFd1Mbps, fd: true);

        var received = new System.Collections.Concurrent.ConcurrentBag<CanFrame>();
        channel.FrameReceived += f => received.Add(f);

        var sender = new BackgroundFrameSender(channel, NullLogger.Instance);
        sender.Start(new[] { MakeFrame(0x123, periodMs: 20, data: new byte[] { 0xAA }) });

        // 等待几帧发送
        await Task.Delay(150);
        sender.Stop();
        sender.Dispose();

        received.Should().NotBeEmpty();
        received.Should().Contain(f => f.Id.Raw == 0x123u);
    }

    [Fact]
    public async Task UpdateFrameData_KnownId_ChangesFrameContent()
    {
        var channel = new VirtualChannel();
        await channel.ConnectAsync(BaudRate.CanFd1Mbps, fd: true);

        var received = new System.Collections.Concurrent.ConcurrentBag<CanFrame>();
        channel.FrameReceived += f => received.Add(f);

        var sender = new BackgroundFrameSender(channel, NullLogger.Instance);
        sender.Start(new[] { MakeFrame(0x200, periodMs: 20, data: new byte[] { 0x01 }) });

        await Task.Delay(80); // 等几帧旧数据

        // 替换数据
        sender.UpdateFrameData(new CanId(0x200, FrameFormat.Standard), new byte[] { 0xFF });

        await Task.Delay(80); // 等几帧新数据
        sender.Stop();
        sender.Dispose();

        // 验证新数据被发送
        var hasNewData = received.Any(f => f.Data.Span.ToArray().SequenceEqual(new byte[] { 0xFF }));
        hasNewData.Should().BeTrue("expected to receive frames with updated data 0xFF");
    }

    [Fact]
    public void UpdateFrameData_UnknownId_LogsWarning()
    {
        var channel = new VirtualChannel();
        var sender = new BackgroundFrameSender(channel, NullLogger.Instance);

        // 不抛异常即可
        sender.UpdateFrameData(new CanId(0x7FE, FrameFormat.Standard), new byte[] { 1 });
    }

    [Fact]
    public void Stop_WithoutStart_NoOp()
    {
        var channel = new VirtualChannel();
        var sender = new BackgroundFrameSender(channel, NullLogger.Instance);

        // 不抛异常即可
        sender.Stop();
        sender.Dispose();
    }

    [Fact]
    public async Task Dispose_WhileRunning_NoCrash()
    {
        var channel = new VirtualChannel();
        await channel.ConnectAsync(BaudRate.CanFd1Mbps, fd: true);

        var sender = new BackgroundFrameSender(channel, NullLogger.Instance);
        sender.Start(new[] { MakeFrame(0x300, periodMs: 10) });

        await Task.Delay(50);
        // 直接 Dispose 不先 Stop
        sender.Dispose();

        // 不抛异常即可
    }
}
