using PeakCan.Host.Core;
using PeakCan.Host.Core.HIL.Gateway;
using PeakCan.Host.Infrastructure.Channel.Gateway;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests.Channel.Gateway;

/// <summary>
/// CanBusGateway unit tests — 单向/双向转发、ID 过滤/映射、防回环（时间窗 + 指纹不含 Channel/Timestamp +
/// 映射第一轮命中）、失败隔离、Dispose 只退订。
/// </summary>
public sealed class CanBusGatewayTests
{
    private static readonly ChannelId ChA = new(0x51);
    private static readonly ChannelId ChB = new(0x52);

    private sealed class FakeChannel : ICanChannel
    {
        public ChannelId Id { get; }
        public bool IsConnected { get; set; } = true;
        public bool Disposed { get; private set; }
        public List<CanFrame> Written { get; } = new();
        // 跨线程安全写计数（并发测试用；Written.List 仅单线程场景断言）。
        public int WriteCount;
        public Func<CanFrame, ValueTask<Result<Unit>>>? WriteHandler { get; set; }
        public event Action<CanFrame>? FrameReceived;
#pragma warning disable CS0067  // ICanChannel contract 要求的事件，测试未订阅（对齐 SendServiceTests）
        public event Action<ReadLoopError>? ReadLoopError;
#pragma warning restore CS0067

        public FakeChannel(ChannelId id) => Id = id;

        /// <summary>模拟总线收到帧（读循环 emit）。</summary>
        public void Emit(CanFrame frame) => FrameReceived?.Invoke(frame);

        public Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default)
        {
            IsConnected = true;
            return Task.FromResult(Result<Unit>.Ok(default));
        }

        public Task DisconnectAsync(CancellationToken ct = default)
        {
            IsConnected = false;
            return Task.CompletedTask;
        }

        public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
        {
            Interlocked.Increment(ref WriteCount);
            if (WriteHandler is not null) return WriteHandler(frame);
            Written.Add(frame);
            return ValueTask.FromResult(Result<Unit>.Ok(default));
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private static CanFrame MakeFrame(uint id, byte[] data, ChannelId channel,
        FrameFormat format = FrameFormat.Standard, FrameFlags flags = FrameFlags.None, ulong timestampUs = 0)
        => new(new CanId(id, format), data, flags, channel, Timestamp.FromMicroseconds(timestampUs));

    private static GatewayConfig Config(
        string target = "USB2", bool bidirectional = false,
        uint? min = null, uint? max = null, uint? map = null)
        => new(target, bidirectional, min, max, map);

    // --- 转发 ---

    [Fact]
    public void Forward_SingleDirection_TargetReceivesFrame()
    {
        var src = new FakeChannel(ChA);
        var dst = new FakeChannel(ChB);
        var gateway = new CanBusGateway(src, dst, Config());
        gateway.Start();

        var frame = MakeFrame(0x100, new byte[] { 1, 2, 3 }, ChA);
        src.Emit(frame);

        var written = Assert.Single(dst.Written);
        Assert.Equal(frame.Data.ToArray(), written.Data.ToArray());
        // L1: 转发帧 Channel 重写为目标通道。
        Assert.Equal(ChB, written.Channel);
    }

    [Fact]
    public void Forward_CanIdFilter_RangeExcluded()
    {
        var src = new FakeChannel(ChA);
        var dst = new FakeChannel(ChB);
        var gateway = new CanBusGateway(src, dst, Config(min: 0x100, max: 0x200));
        gateway.Start();

        src.Emit(MakeFrame(0x050, new byte[] { 0 }, ChA));  // 范围外
        src.Emit(MakeFrame(0x100, new byte[] { 1 }, ChA));  // 边界含
        src.Emit(MakeFrame(0x150, new byte[] { 2 }, ChA));  // 范围内
        src.Emit(MakeFrame(0x250, new byte[] { 3 }, ChA));  // 范围外

        Assert.Equal(new uint[] { 0x100, 0x150 }, dst.Written.Select(w => w.Id.Raw));
    }

    [Fact]
    public void Forward_MapToCanId_RewritesId()
    {
        // map ≤ 0x7FF: 改写 Id、保持 Standard
        var src = new FakeChannel(ChA);
        var dst = new FakeChannel(ChB);
        var gateway = new CanBusGateway(src, dst, Config(map: 0x200));
        gateway.Start();

        src.Emit(MakeFrame(0x100, new byte[] { 1 }, ChA));
        var w1 = Assert.Single(dst.Written);
        Assert.Equal(0x200u, w1.Id.Raw);
        Assert.False(w1.Id.IsExtended);

        // B1: map > 0x7FF -> 目标帧必须是 Extended
        var src2 = new FakeChannel(ChA);
        var dst2 = new FakeChannel(ChB);
        var gateway2 = new CanBusGateway(src2, dst2, Config(map: 0x1234));
        gateway2.Start();

        src2.Emit(MakeFrame(0x100, new byte[] { 1 }, ChA));
        var w2 = Assert.Single(dst2.Written);
        Assert.Equal(0x1234u, w2.Id.Raw);
        Assert.True(w2.Id.IsExtended);
    }

    [Fact]
    public void Forward_Bidirectional_TargetToSource()
    {
        var src = new FakeChannel(ChA);
        var dst = new FakeChannel(ChB);
        var gateway = new CanBusGateway(src, dst, Config(bidirectional: true));
        gateway.Start();

        dst.Emit(MakeFrame(0x300, new byte[] { 9 }, ChB));

        var written = Assert.Single(src.Written);
        Assert.Equal(0x300u, written.Id.Raw);
        Assert.Equal(ChA, written.Channel);
    }

    // --- 防回环 ---

    [Fact]
    public void AntiLoopback_Bidirectional_NoInfiniteLoop()
    {
        // 双向 + loopback 写路径：target 写回后立即 re-emit -> 形成 A→B→A→... 环。防回环应第一轮即断。
        var src = new FakeChannel(ChA);
        var dst = new FakeChannel(ChB);
        src.WriteHandler = frame => { src.Written.Add(frame); src.Emit(frame); return ValueTask.FromResult(Result<Unit>.Ok(default)); };
        dst.WriteHandler = frame => { dst.Written.Add(frame); dst.Emit(frame); return ValueTask.FromResult(Result<Unit>.Ok(default)); };
        var gateway = new CanBusGateway(src, dst, Config(bidirectional: true));
        gateway.Start();

        src.Emit(MakeFrame(0x100, new byte[] { 1, 2, 3 }, ChA));

        // 只应转发一次：target 写 1 次（第一次 source→target 成功），回环帧（target 自己写出的、
        // 经 loopback 回到 source 的帧）被指纹去重丢弃（src 不再写），无无限环。
        Assert.Single(dst.Written);
        Assert.Empty(src.Written);
    }

    [Fact]
    public void AntiLoopback_TimeWindow_Dedup()
    {
        var src = new FakeChannel(ChA);
        var dst = new FakeChannel(ChB);
        var gateway = new CanBusGateway(src, dst, Config());
        gateway.Start();

        var frame = MakeFrame(0x100, new byte[] { 1 }, ChA);
        src.Emit(frame);
        src.Emit(frame);   // 窗口内重复 -> 丢弃

        Assert.Single(dst.Written);

        Thread.Sleep(120);  // 窗口过期（AntiLoopbackWindowMs=100）
        src.Emit(frame);    // 窗口外 -> 再次转发

        Assert.Equal(2, dst.Written.Count);
    }

    [Fact]
    public void AntiLoopback_FingerprintExcludesChannelTimestamp()
    {
        var src = new FakeChannel(ChA);
        var dst = new FakeChannel(ChB);
        var gateway = new CanBusGateway(src, dst, Config());
        gateway.Start();

        // 同 Id + Data + Flags，但 Channel/Timestamp 不同 —— 指纹应命中（不含这两字段）。
        src.Emit(MakeFrame(0x100, new byte[] { 1 }, ChA, timestampUs: 100));
        src.Emit(MakeFrame(0x100, new byte[] { 1 }, ChA, timestampUs: 200));

        Assert.Single(dst.Written);   // 第二次被指纹去重丢弃
    }

    [Fact]
    public void AntiLoopback_MapToCanId_Bidirectional_FirstRoundHit()
    {
        // R1: 映射 + 双向。回环帧 Id 是映射后值（0x200），必须用转发帧指纹才能第一轮命中。
        var src = new FakeChannel(ChA);
        var dst = new FakeChannel(ChB);
        src.WriteHandler = frame => { src.Written.Add(frame); src.Emit(frame); return ValueTask.FromResult(Result<Unit>.Ok(default)); };
        dst.WriteHandler = frame => { dst.Written.Add(frame); dst.Emit(frame); return ValueTask.FromResult(Result<Unit>.Ok(default)); };
        var gateway = new CanBusGateway(src, dst, Config(bidirectional: true, map: 0x200));
        gateway.Start();

        src.Emit(MakeFrame(0x100, new byte[] { 1 }, ChA));

        // 第一次 A→B 写 0x200；B loopback 回 A 时指纹 (0x200,data) 命中 -> 不再转发。无环。
        Assert.Single(dst.Written);
        Assert.Equal(0x200u, dst.Written[0].Id.Raw);
        Assert.Empty(src.Written);
    }

    // --- 错误隔离 / 生命周期 ---

    [Fact]
    public void Forward_WriteFails_NoThrow()
    {
        var src = new FakeChannel(ChA);
        var dst = new FakeChannel(ChB);
        dst.WriteHandler = _ => throw new InvalidOperationException("hardware write failed");
        var gateway = new CanBusGateway(src, dst, Config());
        gateway.Start();

        // H2: WriteSafeAsync 捕获异常，Emit 不抛。
        src.Emit(MakeFrame(0x100, new byte[] { 1 }, ChA));
        Assert.Empty(dst.Written);   // 写失败，无帧落 Written
    }

    [Fact]
    public void Forward_WriteReturnsFailure_NoThrow()
    {
        var src = new FakeChannel(ChA);
        var dst = new FakeChannel(ChB);
        dst.WriteHandler = _ => ValueTask.FromResult(Result<Unit>.Fail(ErrorCode.IoError, "bus error"));
        var gateway = new CanBusGateway(src, dst, Config());
        gateway.Start();

        src.Emit(MakeFrame(0x100, new byte[] { 1 }, ChA));
        // 返回失败 Result 只 LogWarning，不抛。
    }

    [Fact]
    public async Task Dispose_Unsubscribes_ChannelsAlive()
    {
        var src = new FakeChannel(ChA);
        var dst = new FakeChannel(ChB);
        var gateway = new CanBusGateway(src, dst, Config());
        gateway.Start();

        await gateway.DisposeAsync();

        src.Emit(MakeFrame(0x100, new byte[] { 1 }, ChA));
        Assert.Empty(dst.Written);                     // 已退订，不再转发
        Assert.False(src.Disposed);                    // M2: 不 dispose channel
        Assert.False(dst.Disposed);
    }

    [Fact]
    public async Task AntiLoopback_ConcurrentInjection_NoDeadlock_CorrectCount()
    {
        // H4 (spec §5): 双向网关 + 两线程并发注入不同帧 —— _recentLock 无死锁、不抛、转发计数正确。
        var src = new FakeChannel(ChA);
        var dst = new FakeChannel(ChB);
        var gateway = new CanBusGateway(src, dst, Config(bidirectional: true));
        gateway.Start();

        const int framesPerThread = 200;
        var t1 = Task.Run(() =>
        {
            for (int i = 0; i < framesPerThread; i++)
                src.Emit(MakeFrame(0x100u + (uint)i, new byte[] { (byte)i }, ChA));
        });
        var t2 = Task.Run(() =>
        {
            for (int i = 0; i < framesPerThread; i++)
                dst.Emit(MakeFrame(0x300u + (uint)i, new byte[] { (byte)i }, ChB));
        });
        await Task.WhenAll(t1, t2);

        // 每线程 200 个不同 Id 帧 → 全部转发（无死锁、无丢弃）；WriteCount 是 Interlocked 跨线程安全计数。
        Assert.Equal(framesPerThread, dst.WriteCount);
        Assert.Equal(framesPerThread, src.WriteCount);
        await gateway.DisposeAsync();
    }
}
