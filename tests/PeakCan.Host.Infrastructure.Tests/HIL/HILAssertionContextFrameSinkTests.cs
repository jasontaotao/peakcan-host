using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.HIL;

namespace PeakCan.Host.Infrastructure.Tests.HIL;

public class HILAssertionContextFrameSinkTests
{
    private sealed class RecordingSink : IHilFrameSink
    {
        public List<CanFrame> Frames { get; } = new();
        public void Write(CanFrame f) => Frames.Add(f);
        public void Dispose() { }
    }

    // MakeContext / PushFrames 基建：
    // 复用 HIL 命名空间下的公共 FakeCanChannel（loopback：Connect 后 WriteAsync 会同步触发
    // FrameReceived → FrameReceivedSubscription → HILAssertionContext.OnFrame → 帧进入 _frameChannel）。
    // FakeDbcLookup 一律返回 null，consumer 走"未知帧 → 空 signals"路径，纯测 sink 写帧。
    private static readonly Dictionary<HILAssertionContext, FakeCanChannel> Channels = new();

    private static HILAssertionContext MakeContext()
    {
        var channel = new FakeCanChannel();
        // loopback 需要已连接：未连接时 WriteAsync 返回 Fail 且不触发 FrameReceived
        channel.ConnectAsync(BaudRate.Can500kbps, false).GetAwaiter().GetResult();
        var ctx = new HILAssertionContext(channel, new FakeDbcLookup());
        Channels[ctx] = channel;
        // 预热：确保 consumer 线程已启动，避免 drain 测试在 500ms 上限内 consumer 尚未开跑
        Thread.Sleep(20);
        return ctx;
    }

    private static void PushFrames(HILAssertionContext ctx, int n)
    {
        var channel = Channels[ctx];
        for (int i = 0; i < n; i++)
        {
            var frame = new CanFrame(new CanId(0x100, FrameFormat.Standard),
                new byte[] { (byte)(i & 0xFF) }, FrameFlags.None, new ChannelId(1), new Timestamp((ulong)i));
            var vt = channel.WriteAsync(frame, default);
            // Fake 同步完成（IsCompleted 恒 true）；显式传播潜在异常以避免 CA2012
            if (!vt.IsCompleted) _ = vt.AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>Minimal IDbcLookup stub（无 DBC 消息 → 全部按未知帧处理）。</summary>
    private sealed class FakeDbcLookup : IDbcLookup
    {
        public PeakCan.HIL.Core.Dbc.Message? FindMessage(uint canId) => null;
        public IEnumerable<PeakCan.HIL.Core.Dbc.Message> GetAllMessages() =>
            Array.Empty<PeakCan.HIL.Core.Dbc.Message>();
    }

    [Fact]
    public async Task SetFrameSink_FramesWritten_ThenDetachStops()
    {
        var ctx = MakeContext();
        using var sink = new RecordingSink();
        ctx.SetFrameSink(sink);
        PushFrames(ctx, 3);                       // 灌 3 帧
        using var drainCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await ctx.WaitForFrameDrainAsync(drainCts.Token);  // 等待消费
        Assert.Equal(3, sink.Frames.Count);
        ctx.SetFrameSink(null);
        PushFrames(ctx, 2);
        await Task.Delay(50);
        Assert.Equal(3, sink.Frames.Count);       // detach 后不再写
        ctx.Dispose();
    }

    [Fact]
    public async Task WaitForFrameDrain_DrainsBacklog()
    {
        var ctx = MakeContext();
        using var sink = new RecordingSink();
        ctx.SetFrameSink(sink);
        PushFrames(ctx, 100);
        await ctx.WaitForFrameDrainAsync(default);
        Assert.Equal(100, sink.Frames.Count);
        ctx.Dispose();
    }

    [Fact]
    public async Task WaitForFrameDrain_Cancelled_ReturnsWithoutThrow()
    {
        var ctx = MakeContext();
        using var sink = new RecordingSink();
        ctx.SetFrameSink(sink);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Null(await Record.ExceptionAsync(() => ctx.WaitForFrameDrainAsync(cts.Token)));
        ctx.Dispose();
    }

    [Fact]
    public async Task ConcurrentWriteAndDispose_NoObjectDisposedException()
    {
        var ctx = MakeContext();
        using var sink = new RecordingSink();
        ctx.SetFrameSink(sink);
        var t = Task.Run(() => { for (int i = 0; i < 500; i++) PushFrames(ctx, 1); });
        ctx.Dispose();                              // 与写竞态
        await t;
    }
}
