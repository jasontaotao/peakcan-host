using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using PeakCan.HIL.Core;
using PeakCan.Host.Core.Xcp.Abstractions;
using PeakCan.Host.Core.Xcp.Protocol;
using PeakCan.Host.Core.Xcp.Scheduling;

namespace PeakCan.Host.Core.Tests.Xcp.Scheduling;

/// <summary>
/// S2-T12 PollingScheduler（spec §1 获取方式 + §3 Scheduling）：
/// 轮询（SET_MTA+UPLOAD / SHORT_UPLOAD）是位域与 >4B 量的唯一路径、低频兜底；
/// 节奏周期可注入（FakeTimeProvider 驱动）；请求帧逐字节钉死；
/// <b>互斥规则（定死）</b>：轮询在 DAQ 轮转周期内让位——RotationScheduler 的
/// stop→重写→start 进行中（<see cref="IXcpRotationGate"/> 关闭）时，轮询整拍跳过
/// （不排队、不等待、不发任何线上帧），下一个轮询周期再试。
/// 选择理由：XcpMaster 单发单收（排队串扰无意义）；轮转空窗上界已由 T11 钉死，
/// 轮询插队会拉长空窗并违反换表时序假设；轮询是低频兜底，丢一拍无业务损失。
/// RotationScheduler 侧零改动：gate 由组合根（T13）在调用 ConfigureRotationAsync
/// 前后开关——比反向轮询 busy 标志更小侵入。
/// </summary>
public class PollingSchedulerTests
{
    private static readonly CanId MasterCanId = new(0x18FFF667, FrameFormat.Extended);

    private readonly FakeTimeProvider _time = new();

    // ---- 路径一：SHORT_UPLOAD（≤7B 且物理地址 ≤24bit，单帧最省总线）----
    [Fact]
    public async Task PollOnce_uses_short_upload_for_small_entry_within_24bit_address()
    {
        var payload = new byte[] { 0xDE, 0xAD };
        var slave = new ScriptedPollingSlave(payload);
        using var master = new XcpMaster(slave, MasterOptions(), _time);
        var scheduler = new PollingScheduler(
            master, Map(Entry("BitField", byteLength: 2, physical: 0x00001234)), timeProvider: _time);

        var result = await scheduler.PollOnceAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(PollingCycleOutcome.Executed, result.Outcome);
        var value = Assert.Single(result.Values);
        Assert.Equal("BitField", value.ObjectName);
        Assert.Equal(payload, value.Data);

        // 请求帧逐字节：[F4, 00, 00, nbytes, addrExt, addr 低 3B LE]（ADDR_EXT free 基线恒 0）。
        var request = Assert.Single(slave.Sent);
        Assert.Equal(new byte[] { 0xF4, 0x00, 0x00, 0x02, 0x00, 0x34, 0x12, 0x00 }, request);
    }

    // ---- 路径二：SET_MTA + UPLOAD 分块（>7B 量，或地址超 SHORT_UPLOAD 24bit 上限）----
    [Theory]
    [InlineData(8, 0x00001234u)]  // >7B：低地址也必须分块（SHORT_UPLOAD 单帧装不下）
    [InlineData(2, 0x1ABCDEF0u)]  // >24bit：小量也必须走 SET_MTA（SHORT_UPLOAD 地址上限）
    public async Task PollOnce_uses_set_mta_then_upload_chunks_when_outside_short_upload_domain(
        int byteLength, uint address)
    {
        var payload = Enumerable.Range(0, byteLength).Select(i => (byte)(0x10 + i)).ToArray();
        var slave = new ScriptedPollingSlave(payload);
        using var master = new XcpMaster(slave, MasterOptions(), _time);
        var scheduler = new PollingScheduler(
            master, Map(Entry("Object", byteLength, address)), timeProvider: _time);

        var result = await scheduler.PollOnceAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(PollingCycleOutcome.Executed, result.Outcome);
        Assert.Equal(payload, Assert.Single(result.Values).Data);

        // 请求帧逐字节：SET_MTA 一次（MTA 按 XCP 标准随 UPLOAD 后自增），UPLOAD 按 7B 分块。
        var expected = new List<byte[]>
        {
            new byte[]
            {
                0xF6, 0x00, 0x00, 0x00,
                (byte)address, (byte)(address >> 8), (byte)(address >> 16), (byte)(address >> 24),
            },
        };
        var remaining = byteLength;
        while (remaining > 0)
        {
            var chunk = Math.Min(7, remaining);
            expected.Add(new byte[] { 0xF5, 0x00, 0x00, (byte)chunk, 0x00, 0x00, 0x00, 0x00 });
            remaining -= chunk;
        }
        Assert.Equal(expected, slave.Sent);
    }

    // ---- fail-loud：物理地址未翻译（[H1]：轮询地址必经包侧唯一入口翻译）----
    [Fact]
    public void Ctor_fails_loud_when_polling_entry_has_no_translated_physical_address()
    {
        var slave = new ScriptedPollingSlave(new byte[] { 0x01 });
        using var master = new XcpMaster(slave, MasterOptions(), _time);
        var entry = new PlannedPollingEntry(
            "Untranslated", 0, 8, 0, LogicalAddress: 0x1000, PhysicalAddress: null,
            PlannedPollingCause.ObjectTooLarge);

        var ex = Assert.Throws<InvalidOperationException>(
            () => new PollingScheduler(master, Map(entry), timeProvider: _time));
        Assert.Contains("Untranslated", ex.Message);
        Assert.Empty(slave.Sent); // 线上零流量
    }

    // ---- 低频兜底节奏：周期可注入，FakeTimeProvider 驱动 ----
    [Theory]
    [InlineData(250)]
    [InlineData(1000)]
    public async Task RunAsync_fires_one_cycle_per_injected_period(int periodMs)
    {
        var period = TimeSpan.FromMilliseconds(periodMs);
        var slave = new ScriptedPollingSlave(new byte[] { 0x2A });
        using var master = new XcpMaster(slave, MasterOptions(), _time);
        var scheduler = new PollingScheduler(
            master,
            Map(Entry("Small", byteLength: 1, physical: 0x10)),
            new PollingSchedulerOptions { Period = period },
            timeProvider: _time);
        Assert.Equal(period, scheduler.Period);

        var results = new ConcurrentQueue<PollingCycleResult>();
        var cycleCount = 0;
        using var cts = new CancellationTokenSource();
        var run = scheduler.RunAsync(r => { results.Enqueue(r); Interlocked.Increment(ref cycleCount); }, cts.Token);

        Assert.True(results.IsEmpty); // 首拍前不轮询（等待先行）

        _time.Advance(period);
        await AdvanceUntil(() => Volatile.Read(ref cycleCount) >= 1, run: run);
        _time.Advance(period);
        await AdvanceUntil(() => Volatile.Read(ref cycleCount) >= 2);

        Assert.Equal(2, slave.Sent.Count); // 每个周期恰好一拍
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    // ---- 互斥规则（定死）：轮转进行中轮询让位——单拍跳过、零线上流量 ----
    [Fact]
    public async Task PollOnce_yields_to_rotation_in_progress_with_zero_wire_traffic()
    {
        var gate = new TestRotationGate { InRotation = true };
        var slave = new ScriptedPollingSlave(new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 });
        using var master = new XcpMaster(slave, MasterOptions(), _time);
        var scheduler = new PollingScheduler(
            master, Map(Entry("Obj", 8, 0x1000)), rotationGate: gate, timeProvider: _time);

        var result = await scheduler.PollOnceAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(PollingCycleOutcome.SkippedRotation, result.Outcome);
        Assert.Empty(result.Values);
        Assert.Empty(slave.Sent); // 让位 = 不排队不发帧，不消耗 XcpMaster 单发单收窗口

        gate.InRotation = false; // 轮转结束 → 下一拍恢复
        var resumed = await scheduler.PollOnceAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PollingCycleOutcome.Executed, resumed.Outcome);
        Assert.Equal(3, slave.Sent.Count); // SET_MTA + UPLOAD(7) + UPLOAD(1)
    }

    // ---- 互斥规则：周期循环里跳过轮转拍、开门后自动恢复 ----
    [Fact]
    public async Task RunAsync_skips_cycles_during_rotation_and_resumes_after_gate_opens()
    {
        var gate = new TestRotationGate { InRotation = true };
        var period = TimeSpan.FromMilliseconds(250);
        var slave = new ScriptedPollingSlave(new byte[] { 0x2A });
        using var master = new XcpMaster(slave, MasterOptions(), _time);
        var scheduler = new PollingScheduler(
            master,
            Map(Entry("Small", byteLength: 1, physical: 0x10)),
            new PollingSchedulerOptions { Period = period },
            rotationGate: gate,
            timeProvider: _time);

        var results = new ConcurrentQueue<PollingCycleResult>();
        var cycleCount = 0;
        using var cts = new CancellationTokenSource();
        var run = scheduler.RunAsync(r => { results.Enqueue(r); Interlocked.Increment(ref cycleCount); }, cts.Token);

        _time.Advance(period);
        await AdvanceUntil(() => Volatile.Read(ref cycleCount) >= 1, run: run);
        Assert.Equal(PollingCycleOutcome.SkippedRotation, results.First().Outcome);
        Assert.Empty(slave.Sent);

        gate.InRotation = false;
        _time.Advance(period);
        await AdvanceUntil(() => Volatile.Read(ref cycleCount) >= 2);

        Assert.Single(slave.Sent); // 只有开门后的那一拍上了总线
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    // ---- 单飞：与 RotationScheduler 同型（XcpMaster 单发单收，排队即串扰）----
    [Fact]
    public async Task PollOnce_rejects_concurrent_invocation_while_previous_cycle_pending()
    {
        var slave = new ScriptedPollingSlave(new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 });
        using var master = new XcpMaster(slave, MasterOptions(), _time);
        var scheduler = new PollingScheduler(
            master, Map(Entry("Big", 8, 0x1000)), timeProvider: _time);

        slave.HoldUpload();
        var first = scheduler.PollOnceAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => scheduler.PollOnceAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("single-flight", ex.Message);

        slave.ReleaseHold();
        var result = await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 }, Assert.Single(result.Values).Data);
    }

    // ---- 空轮询集合：零线上流量 ----
    [Fact]
    public async Task PollOnce_with_no_polling_entries_produces_no_traffic()
    {
        var slave = new ScriptedPollingSlave(Array.Empty<byte>());
        using var master = new XcpMaster(slave, MasterOptions(), _time);
        var scheduler = new PollingScheduler(
            master,
            new PlannedAcquisitionMap(0, 0, Array.Empty<PlannedOdt>(), Array.Empty<PlannedPollingEntry>()),
            timeProvider: _time);

        var result = await scheduler.PollOnceAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(PollingCycleOutcome.NoEntries, result.Outcome);
        Assert.Empty(slave.Sent);
    }

    // ---- helpers ----

    private static XcpMasterOptions MasterOptions() => new(MasterCanId);

    private static PlannedPollingEntry Entry(string name, int byteLength, ulong physical) =>
        new(name, 0, byteLength, 0, physical, physical, PlannedPollingCause.ObjectTooLarge);

    private static PlannedAcquisitionMap Map(params PlannedPollingEntry[] entries) =>
        new(0, 0, Array.Empty<PlannedOdt>(), entries);

    private async Task AdvanceUntil(Func<bool> condition, TimeSpan? step = null, int timeoutMs = 5000, Task? run = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (run is { IsFaulted: true }) throw run.Exception!.Flatten().InnerException ?? run.Exception!;
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException($"fake-time condition not met within {timeoutMs} ms");
            _time.Advance(step ?? TimeSpan.FromMilliseconds(1));
            await Task.Delay(1);
        }
    }

    /// <summary>轮转 gate 测试替身：组合根在 ConfigureRotationAsync 前后开关。</summary>
    private sealed class TestRotationGate : IXcpRotationGate
    {
        public bool InRotation;
        public bool IsRotationInProgress => InRotation;
    }

    /// <summary>
    /// 脚本化轮询从机：UPLOAD/SHORT_UPLOAD 按配置的载荷顺序吐数据
    /// （MTA 按 XCP 标准随 UPLOAD 后自增），SET_MTA 记录并正应答。
    /// </summary>
    private sealed class ScriptedPollingSlave : IXcpTransport
    {
        private readonly byte[] _payload;
        private int _cursor;
        private uint _mta;
        private TaskCompletionSource<object?>? _hold;

        public ScriptedPollingSlave(byte[] payload) => _payload = payload;

        public event Action<CanFrame>? FrameReceived;

        public long FramesDropped => 0;

        public List<byte[]> Sent { get; } = new();

        public void HoldUpload() =>
            _hold = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseHold() => _hold?.TrySetResult(null);

        public async ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
        {
            var cmd = frame.Data.ToArray();
            Sent.Add(cmd);
            if (_hold is not null && cmd[0] == XcpPid.Upload)
                await _hold.Task.ConfigureAwait(false);
            Respond(cmd);
            return Result<Unit>.Ok(default);
        }

        private void Respond(byte[] cmd)
        {
            switch (cmd[0])
            {
                case XcpPid.SetMta:
                    _mta = ReadU32(cmd, 4);
                    _cursor = 0; // 新一次读起点
                    Positive();
                    break;
                case XcpPid.Upload or XcpPid.ShortUpload:
                {
                    var count = cmd[3];
                    // SHORT_UPLOAD 自包含单帧读（恒从载荷起点）；UPLOAD 按 MTA 连续分块。
                    var offset = cmd[0] == XcpPid.Upload ? _cursor : 0;
                    var data = new byte[count];
                    Array.Copy(_payload, offset, data, 0, count);
                    if (cmd[0] == XcpPid.Upload)
                        _cursor += count;
                    Emit(data);
                    break;
                }
                default:
                    Positive();
                    break;
            }
        }

        private void Positive() => Emit(Array.Empty<byte>());

        private void Emit(byte[] data)
        {
            var frame = new byte[1 + data.Length];
            frame[0] = XcpPid.PositiveResponse;
            data.CopyTo(frame, 1);
            FrameReceived?.Invoke(new CanFrame(
                new CanId(0x18FFF666, FrameFormat.Extended),
                new ReadOnlyMemory<byte>(frame),
                FrameFlags.None,
                ChannelId.None,
                default));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static uint ReadU32(byte[] frame, int offset) =>
            frame[offset] | ((uint)frame[offset + 1] << 8) | ((uint)frame[offset + 2] << 16) | ((uint)frame[offset + 3] << 24);
    }
}