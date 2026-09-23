using NSubstitute;
using PeakCan.HIL.Core;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Xcp.Abstractions;
using PeakCan.Host.Core.Xcp.Protocol;
using PeakCan.Host.Infrastructure.Xcp;

namespace PeakCan.Host.Infrastructure.Tests.Xcp;

/// <summary>
/// XcpCanTransport 适配层：读线程只入队（不阻塞）、WriteAsync 透传、
/// 订阅者回调中写帧不死锁（spec §3 Infrastructure）。
/// </summary>
public class XcpCanTransportTests
{
    private static CanFrame Frame(byte pid) => new(
        new CanId(0x18FFF666, FrameFormat.Extended),
        new ReadOnlyMemory<byte>(new byte[] { pid, 0x01, 0x02 }),
        FrameFlags.None,
        ChannelId.None,
        default);

    [Fact]
    public async Task Channel_frame_is_forwarded_to_FrameReceived()
    {
        var channel = Substitute.For<ICanChannel>();
        await using var transport = new XcpCanTransport(channel);

        var received = new TaskCompletionSource<CanFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.FrameReceived += f => received.TrySetResult(f);

        var sent = Frame(XcpPid.PositiveResponse);
        channel.FrameReceived += Raise.Event<Action<CanFrame>>(sent);

        var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(sent.Id, got.Id);
        Assert.Equal(sent.Data.ToArray(), got.Data.ToArray());
    }

    [Fact]
    public async Task Forwarded_frames_keep_order()
    {
        var channel = Substitute.For<ICanChannel>();
        await using var transport = new XcpCanTransport(channel);

        var received = new List<byte>();
        var allDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.FrameReceived += f =>
        {
            lock (received)
            {
                received.Add(f.Data.Span[0]);
                if (received.Count == 3) allDone.TrySetResult();
            }
        };

        foreach (var pid in new byte[] { 0x00, 0x01, 0x02 })
            channel.FrameReceived += Raise.Event<Action<CanFrame>>(Frame(pid));

        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new byte[] { 0x00, 0x01, 0x02 }, received);
    }

    [Fact]
    public async Task WriteAsync_passes_through_to_channel()
    {
        var channel = Substitute.For<ICanChannel>();
        await using var transport = new XcpCanTransport(channel);

        var frame = Frame(XcpPid.Connect);
        channel.WriteAsync(frame, Arg.Any<CancellationToken>())
            .Returns(Result<Unit>.Ok(default));

        var result = await transport.WriteAsync(frame);

        Assert.True(result.IsSuccess);
        await channel.Received(1).WriteAsync(frame, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Write_inside_FrameReceived_callback_does_not_deadlock()
    {
        var channel = new BlockingWriteChannel();
        await using var transport = new XcpCanTransport(channel);

        var written = new TaskCompletionSource<Result<Unit>>(TaskCreationOptions.RunContinuationsAsynchronously);
        // 分发线程内的订阅者同步等待写完成：只有“读线程只入队”的设计下，
        // 读线程才能先返回、测试线程才能放行写门。
        transport.FrameReceived += f =>
        {
            var result = transport.WriteAsync(f).AsTask().GetAwaiter().GetResult();
            written.TrySetResult(result);
        };

        // 模拟读线程发帧：raise 调用必须立即返回（不得被订阅者阻塞）。
        await Task.Run(() => channel.RaiseFrame(Frame(XcpPid.PositiveResponse)))
            .WaitAsync(TimeSpan.FromSeconds(2));

        channel.WriteGate.TrySetResult(Result<Unit>.Ok(default));

        var result = await written.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Read_thread_never_blocks_when_queue_is_full()
    {
        var channel = Substitute.For<ICanChannel>();
        await using var transport = new XcpCanTransport(channel, queueCapacity: 4);

        // 无订阅者 + 队列超容量：入队必须全部即时成功（丢旧，不阻塞读线程）。
        for (var i = 0; i < 1000; i++)
            channel.FrameReceived += Raise.Event<Action<CanFrame>>(Frame(0x00));

        // 排空屏障：哨兵帧（0x0F）派发即代表此前全部入队帧已处理完毕。
        // 没有它，分发线程追赶积压期间订阅会先收到积压 DTO（先来先派发
        // 的固有 FIFO 竞态），断言会随机失败。
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.FrameReceived += f =>
        {
            if (f.Data.Span[0] == 0x0F) drained.TrySetResult();
        };
        channel.FrameReceived += Raise.Event<Action<CanFrame>>(Frame(0x0F));
        await drained.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var received = new TaskCompletionSource<CanFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.FrameReceived += f => received.TrySetResult(f);
        var sent = Frame(0x01);
        channel.FrameReceived += Raise.Event<Action<CanFrame>>(sent);

        var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(sent.Data.ToArray(), got.Data.ToArray());
    }

    [Theory]
    [InlineData(XcpPid.PositiveResponse)]
    [InlineData(XcpPid.Error)]
    public async Task Response_frames_survive_dto_flood_and_drops_are_counted(byte responsePid)
    {
        var channel = Substitute.For<ICanChannel>();
        await using var transport = new XcpCanTransport(channel, queueCapacity: 4);

        var firstDtoDispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDto = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var responsesSeen = 0;

        transport.FrameReceived += f =>
        {
            var pid = f.Data.Span[0];
            if (pid == XcpPid.PositiveResponse || pid == XcpPid.Error)
            {
                responsesSeen++;
                return;
            }

            // 首个 DTO 到达分发端后阻塞分发循环，制造确定性的洪泛积压，
            // 保证后续 DTO 入队必然触发 DropOldest（丢帧计数可断言）。
            firstDtoDispatched.TrySetResult();
            releaseDto.Task.Wait(TimeSpan.FromSeconds(5));
        };

        // 响应先入队，随后 DTO 洪泛挤占有界队列：修复前 DropOldest 会把
        // 已入队的响应挤掉（红）；修复后响应走独立通道永不丢失（绿）。
        channel.FrameReceived += Raise.Event<Action<CanFrame>>(Frame(responsePid));
        for (var i = 0; i < 1000; i++)
            channel.FrameReceived += Raise.Event<Action<CanFrame>>(Frame(XcpPid.DaqDtoFirst));

        await firstDtoDispatched.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // 响应在任何 DTO 之前派发（响应优先通道），且洪泛期间不得被丢弃。
        Assert.Equal(1, responsesSeen);
        Assert.True(transport.FramesDropped > 0, "DTO 洪泛必须产生可观测的丢帧计数");

        releaseDto.TrySetResult();
    }
    /// <summary>
    /// 写门可控的假通道：WriteAsync 挂在 TaskCompletionSource 上，
    /// 用于复现“订阅者在回调里同步等待写完成”的死锁场景。
    /// </summary>
    private sealed class BlockingWriteChannel : ICanChannel
    {
        public TaskCompletionSource<Result<Unit>> WriteGate { get; }
            = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event Action<CanFrame>? FrameReceived;

        public event Action<ReadLoopError>? ReadLoopError { add { } remove { } }

        public ChannelId Id => ChannelId.None;

        public bool IsConnected => true;

        public Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default)
            => Task.FromResult(Result<Unit>.Ok(default));

        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
            => new(WriteGate.Task);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void RaiseFrame(CanFrame frame) => FrameReceived?.Invoke(frame);
    }
}



