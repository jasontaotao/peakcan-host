using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using PeakCan.HIL.Core;
using PeakCan.Host.Core.Xcp.Abstractions;
using PeakCan.Host.Core.Xcp.Protocol;

namespace PeakCan.Host.Core.Tests.Xcp.Protocol;

/// <summary>
/// XcpMaster 单发单收会话引擎（spec §3 Protocol 超时策略 + T2 评审补遗决策点）：
/// pending-response 状态机、T1=2000ms 超时、重试 N=1、EV/DTO 帧消费忽略、
/// ERR_CMD_SYNCH 不自动重发、超时归因查 FramesDropped。
/// </summary>
public class XcpMasterTests
{
    private static readonly TimeSpan T1 = TimeSpan.FromMilliseconds(2000);

        private readonly FakeTimeProvider _time = new();

    private static readonly CanId MasterCanId = new(0x18FFF667, FrameFormat.Extended);

    [Fact]
    public async Task Positive_response_completes_pending_request()
    {
        var transport = new FakeXcpTransport();
        using var master = new XcpMaster(transport, new XcpMasterOptions(MasterCanId), _time);

        var task = master.SendAsync(XcpCommandEncoder.Synch());
        transport.SlaveSend(XcpPid.PositiveResponse, 0xAA);

        var response = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new byte[] { XcpPid.PositiveResponse, 0xAA }, response);
        Assert.Equal(1, transport.WriteCount);
    }

    // T2 评审决策 (a)：EV 0xFD 帧消费并忽略，不算协议错误，不干扰 pending 配对。
    [Fact]
    public async Task Ev_frame_is_consumed_and_ignored_while_pending()
    {
        var transport = new FakeXcpTransport();
        using var master = new XcpMaster(transport, new XcpMasterOptions(MasterCanId), _time);

        var task = master.SendAsync(XcpCommandEncoder.Synch());
        transport.SlaveSend(XcpPid.EventPacket, 0x05, 0x00); // EV_SLAVE_CMD_SYNC 等
        transport.SlaveSend(XcpPid.PositiveResponse);

        await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, transport.WriteCount);
    }

    [Fact]
    public async Task Ev_frame_without_pending_is_consumed_and_ignored()
    {
        var transport = new FakeXcpTransport();
        using var master = new XcpMaster(transport, new XcpMasterOptions(MasterCanId), _time);

        transport.SlaveSend(XcpPid.EventPacket, 0x00);

        var task = master.SendAsync(XcpCommandEncoder.Synch());
        transport.SlaveSend(XcpPid.PositiveResponse);
        await task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // DTO（PID 0x00-0xFB）与 pending 配对无关：消费忽略，不得当作响应。
    [Fact]
    public async Task Daq_dto_frame_is_ignored_while_pending()
    {
        var transport = new FakeXcpTransport();
        using var master = new XcpMaster(transport, new XcpMasterOptions(MasterCanId), _time);

        var task = master.SendAsync(XcpCommandEncoder.Synch());
        transport.SlaveSend(0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07);
        transport.SlaveSend(XcpPid.PositiveResponse);

        await task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Negative_response_throws_with_error_code_without_retry()
    {
        var transport = new FakeXcpTransport();
        using var master = new XcpMaster(transport, new XcpMasterOptions(MasterCanId), _time);

        var task = master.SendAsync(XcpCommandEncoder.Connect());
        transport.SlaveSend(XcpPid.Error, (byte)XcpError.CmdBusy);

        var ex = await Assert.ThrowsAsync<XcpErrorResponseException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(XcpError.CmdBusy, ex.Response.Code);
        // 负响应即终态：不重试（重试只属于超时路径）。
        Assert.Equal(1, transport.WriteCount);
    }

    // T2 评审决策 (b)：ERR_CMD_SYNCH(0x00) 不自动重发 SYNCH，直接上抛（归因层决定恢复动作）。
    [Fact]
    public async Task Err_cmd_synch_is_thrown_directly_without_auto_resend()
    {
        var transport = new FakeXcpTransport();
        using var master = new XcpMaster(transport, new XcpMasterOptions(MasterCanId), _time);

        var task = master.SendAsync(XcpCommandEncoder.GetStatus());
        transport.SlaveSend(XcpPid.Error, (byte)XcpError.CmdSynch);

        var ex = await Assert.ThrowsAsync<XcpErrorResponseException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(XcpError.CmdSynch, ex.Response.Code);
        // 全程只有原始请求一次发送：无 SYNCH 自动重发。
        Assert.Equal(1, transport.WriteCount);
        Assert.Equal(XcpPid.GetStatus, transport.Sent[0][0]);
    }

    [Fact]
    public async Task Unknown_error_code_fails_promptly_instead_of_hanging()
    {
        var transport = new FakeXcpTransport();
        using var master = new XcpMaster(transport, new XcpMasterOptions(MasterCanId), _time);

        var task = master.SendAsync(XcpCommandEncoder.Synch());
        transport.SlaveSend(XcpPid.Error, 0x99);

        await Assert.ThrowsAnyAsync<Exception>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, transport.WriteCount);
    }

    [Fact]
    public async Task T1_timeout_fails_pending_request()
    {
        var transport = new FakeXcpTransport();
        // MaxRetries=0：单次尝试，隔离验证 T1 超时本身判失败并中止 pending。
        using var master = new XcpMaster(transport, new XcpMasterOptions(MasterCanId, maxRetries: 0), _time);

        var task = master.SendAsync(XcpCommandEncoder.Synch());
        _time.Advance(T1);

        var ex = await Assert.ThrowsAsync<XcpTimeoutException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, ex.Attempts);
        Assert.Equal(T1, ex.Timeout);
        Assert.Equal(1, transport.WriteCount);
    }

    [Fact]
    public async Task Timeout_retries_once_then_succeeds()
    {
        var transport = new FakeXcpTransport();
        using var master = new XcpMaster(transport, new XcpMasterOptions(MasterCanId), _time);

        var task = master.SendAsync(XcpCommandEncoder.Synch());
        _time.Advance(T1); // 第一次尝试超时
        await transport.WaitForWriteAsync(2);

        transport.SlaveSend(XcpPid.PositiveResponse);
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, transport.WriteCount);
    }

    [Fact]
    public async Task Retries_exhausted_throws_timeout_with_slave_no_response_attribution()
    {
        var transport = new FakeXcpTransport();
        using var master = new XcpMaster(transport, new XcpMasterOptions(MasterCanId), _time);

        var task = master.SendAsync(XcpCommandEncoder.Synch());
        _time.Advance(T1);
        await transport.WaitForWriteAsync(2);
        _time.Advance(T1); // 重试也超时，且期间无本机丢帧

        var ex = await Assert.ThrowsAsync<XcpTimeoutException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(XcpTimeoutCause.SlaveNoResponse, ex.Attribution);
        Assert.Equal(0, ex.FramesDroppedDelta);
        Assert.Equal(2, ex.Attempts);
        Assert.Equal(T1, ex.Timeout);
        Assert.Equal(2, transport.WriteCount);
    }

    // 超时归因：FramesDropped 增长 = 本机丢帧（DTO 洪泛挤占），非从机无响应。
    [Fact]
    public async Task Timeout_attribution_is_local_frame_drop_when_frames_dropped_grow()
    {
        var transport = new FakeXcpTransport();
        using var master = new XcpMaster(transport, new XcpMasterOptions(MasterCanId), _time);

        var task = master.SendAsync(XcpCommandEncoder.Synch());
        _time.Advance(T1);
        await transport.WaitForWriteAsync(2);
        transport.DropFrames(3);
        _time.Advance(T1);

        var ex = await Assert.ThrowsAsync<XcpTimeoutException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(XcpTimeoutCause.LocalFrameDrop, ex.Attribution);
        Assert.Equal(3, ex.FramesDroppedDelta);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 3)]
    public async Task Max_retries_is_parameterized_by_options(int maxRetries, int expectedWrites)
    {
        var transport = new FakeXcpTransport();
        using var master = new XcpMaster(transport, new XcpMasterOptions(MasterCanId, maxRetries: maxRetries), _time);

        var task = master.SendAsync(XcpCommandEncoder.Synch());
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            _time.Advance(T1);
            await transport.WaitForWriteAsync(attempt + 1);
        }

        _time.Advance(T1);
        await Assert.ThrowsAsync<XcpTimeoutException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(expectedWrites, transport.WriteCount);
    }

    // 单发单收（无 INTERLEAVED，spec §1）：pending 期间第二个请求拒绝，不排队。
    [Fact]
    public async Task Second_request_during_pending_is_rejected()
    {
        var transport = new FakeXcpTransport();
        using var master = new XcpMaster(transport, new XcpMasterOptions(MasterCanId), _time);

        var first = master.SendAsync(XcpCommandEncoder.Synch());
        await Assert.ThrowsAsync<InvalidOperationException>(() => master.SendAsync(XcpCommandEncoder.GetStatus()));

        transport.SlaveSend(XcpPid.PositiveResponse);
        await first.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // T4 评审：响应等待期间调用方取消 → 立即 OCE，而非挂到 T1 定时器
    //（FakeTimeProvider 不 Advance ⇒ 若仍挂在 T1 上本测试会永久阻塞）。
    [Fact]
    public async Task Caller_cancellation_during_response_wait_throws_immediately()
    {
        var transport = new FakeXcpTransport();
        using var master = new XcpMaster(transport, new XcpMasterOptions(MasterCanId), _time);
        using var cts = new CancellationTokenSource();

        var task = master.SendAsync(XcpCommandEncoder.Synch(), cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        // 取消发生在响应等待期：请求已上总线一次，且未进入重试。
        Assert.Equal(1, transport.WriteCount);
    }

    // 对齐 UdsClient C-8 fix 先例：无 pending 时到达的正响应（迟到/错位帧）丢弃，
    // 让超时语义接管，不得被当作下一个请求的响应。
    [Fact]
    public async Task Stale_positive_response_without_pending_is_dropped()
    {
        var transport = new FakeXcpTransport();
        using var master = new XcpMaster(transport, new XcpMasterOptions(MasterCanId), _time);

        transport.SlaveSend(XcpPid.PositiveResponse, 0xBB); // 迟到的上一命令响应

        var task = master.SendAsync(XcpCommandEncoder.Synch());
        _time.Advance(T1);
        await transport.WaitForWriteAsync(2);
        _time.Advance(T1);

        var ex = await Assert.ThrowsAsync<XcpTimeoutException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(XcpTimeoutCause.SlaveNoResponse, ex.Attribution);
    }

    [Fact]
    public void Options_reject_invalid_timeout_and_retries()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new XcpMasterOptions(MasterCanId, timeout: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new XcpMasterOptions(MasterCanId, maxRetries: -1));
    }

    private sealed class FakeXcpTransport : IXcpTransport
    {
        private readonly List<byte[]> _sent = new();

        public event Action<CanFrame>? FrameReceived;

        public long FramesDropped { get; private set; }

        public IReadOnlyList<byte[]> Sent => _sent;

        public int WriteCount => _sent.Count;

        public void DropFrames(long count) => FramesDropped += count;

        public void SlaveSend(params byte[] data) => FrameReceived?.Invoke(new CanFrame(
            new CanId(0x18FFF666, FrameFormat.Extended),
            new ReadOnlyMemory<byte>(data),
            FrameFlags.None,
            ChannelId.None,
            default));

        public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
        {
            lock (_sent)
                _sent.Add(frame.Data.ToArray());
            return ValueTask.FromResult(Result<Unit>.Ok(default));
        }

        /// <summary>等待第 <paramref name="count"/> 次发送完成（消除重试启动的线程池竞态）。</summary>
        public async Task WaitForWriteAsync(int count)
        {
            var sw = Stopwatch.StartNew();
            while (WriteCount < count)
            {
                if (sw.Elapsed > TimeSpan.FromSeconds(5))
                    throw new TimeoutException($"Expected {count} writes, got {WriteCount}.");
                await Task.Delay(10);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
