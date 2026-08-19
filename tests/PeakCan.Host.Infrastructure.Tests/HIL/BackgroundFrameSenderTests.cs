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

/// <summary>
/// D3-R1: 串行化 BackgroundFrame 时序测试集合——1ms/10ms Timer 周期发送对 CI CPU
/// 争用敏感，与 BackgroundFrameAutoConfigTests 同集合序列化，避免并行加剧 tick
/// 抖动致 flaky。对齐 HILIntegrationCollection 的序列化模式。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BackgroundFrameCollection
{
    public const string Name = "BackgroundFrame";
}

[Collection(BackgroundFrameCollection.Name)]
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

        // D3-R1: 转事件驱动 TCS——收到目标帧即完成，WaitAsync 作硬上限，
        // 取代 Task.Delay(150) + count 断言（Timer tick 在 CI 上抖动致偶发空集）。
        const uint targetId = 0x123u;
        var targetTcs = new TaskCompletionSource<CanFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.FrameReceived += f =>
        {
            if (f.Id.Raw == targetId) targetTcs.TrySetResult(f);
        };

        var sender = new BackgroundFrameSender(channel, NullLogger.Instance);
        sender.Start(new[] { MakeFrame(targetId, periodMs: 20, data: new byte[] { 0xAA }) });
        try
        {
            var frame = await targetTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            frame.Id.Raw.Should().Be(targetId);
            frame.Data.Span.ToArray().Should().Equal(new byte[] { 0xAA });
        }
        finally
        {
            sender.Stop();
            sender.Dispose();
            await channel.DisposeAsync();
        }
    }

    [Fact]
    public async Task UpdateFrameData_KnownId_ChangesFrameContent()
    {
        var channel = new VirtualChannel();
        await channel.ConnectAsync(BaudRate.CanFd1Mbps, fd: true);

        // D3-R1: 转事件驱动 TCS——先等首帧旧数据（证明 sender 已启动），再 UpdateData，
        // 然后等新数据帧；取代双重 Task.Delay(80) + Any 断言（窗口内可能未收齐致 flaky）。
        const uint frameId = 0x200u;
        var firstFrameTcs = new TaskCompletionSource<CanFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var updatedTcs = new TaskCompletionSource<CanFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.FrameReceived += f =>
        {
            if (f.Id.Raw != frameId) return;
            if (f.Data.Span.Length > 0 && f.Data.Span[0] == 0xFF)
                updatedTcs.TrySetResult(f);
            else
                firstFrameTcs.TrySetResult(f);
        };

        var sender = new BackgroundFrameSender(channel, NullLogger.Instance);
        sender.Start(new[] { MakeFrame(frameId, periodMs: 20, data: new byte[] { 0x01 }) });
        try
        {
            await firstFrameTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            sender.UpdateFrameData(new CanId(frameId, FrameFormat.Standard), new byte[] { 0xFF });
            var frame = await updatedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            frame.Data.Span.ToArray().Should().Equal(new byte[] { 0xFF });
        }
        finally
        {
            sender.Stop();
            sender.Dispose();
            await channel.DisposeAsync();
        }
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

        // D3-R1: 转事件驱动 TCS——等首帧到达后再 Dispose，证明 Timer 确已运行；
        // 取代 Task.Delay(50) 盲等（无法保证 tick 已发生）。
        var firstFrameTcs = new TaskCompletionSource<CanFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.FrameReceived += f => firstFrameTcs.TrySetResult(f);

        var sender = new BackgroundFrameSender(channel, NullLogger.Instance);
        sender.Start(new[] { MakeFrame(0x300, periodMs: 10) });
        try
        {
            await firstFrameTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            // 直接 Dispose 不先 Stop
            sender.Dispose();
            await channel.DisposeAsync();
        }

        // 不抛异常即可
    }
}
