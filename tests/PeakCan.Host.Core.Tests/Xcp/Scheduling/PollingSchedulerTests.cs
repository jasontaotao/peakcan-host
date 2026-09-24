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
/// <para>T12 review 增量：失败归因出站不终止 RunAsync（F3）、超时 quiesce ≥T1
/// 时钟断言（F3）、WaitForQuietAsync 静默握手（F4）、8B padding 宽容切片（F2）、
/// 超 32 位地址 fail-loud（F5）。</para>
/// </summary>
public class PollingSchedulerTests
{
    private static readonly CanId MasterCanId = new(0x18FFF667, FrameFormat.Extended);

    /// <summary>T1（XcpMasterOptions.DefaultTimeout）——quiesce 与超时测试的钟源基准。</summary>
    private static readonly TimeSpan T1 = TimeSpan.FromMilliseconds(2000);

    private readonly FakeTimeProvider _time = new();

    // ---- 路径一：SHORT_UPLOAD（≤7B 且物理地址 ≤24bit，单帧最省总线）----
    [Fact]
    public async Task PollOnce_uses_short_upload_for_small_entry_within_24bit_address()
    {
        var payload = new byte[] { 0xDE, 0xAD };
        var slave = new ScriptedPollingSlave(payload);
        using var master = new XcpMaster(slave, MasterOptions(), _time);
        var scheduler = NewScheduler(master, Map(Entry("BitField", byteLength: 2, physical: 0x00001234)));

        var result = await scheduler.PollOnceAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(PollingCycleOutcome.Executed, result.Outcome);
        var value = Assert.Single(result.Values);
        Assert.Equal("BitField", value.ObjectName);
        Assert.Equal(payload, value.Data);

        // 请求帧逐字节：[F4, 00, 00, nbytes, addrExt, addr 低 3B LE]（ADDR_EXT free 基线恒 0）。
        var request = Assert.Single(slave.Sent);
        Assert.Equal(new byte[] { 0xF4, 0x00, 0x00, 0x02, 0x00, 0x34, 0x12, 0x00 }, request);
    }

    // ---- (F2) 正响应允许 DLC 8B padding：取前 expectedBytes，短帧仍 fail-loud ----
    [Fact]
    public async Task PollOnce_tolerates_fixed_dlc_padding_in_upload_response()
    {
        var slave = new ScriptedPollingSlave(new byte[] { 0xA1, 0xB2 }) { PadUploadResponses = true };
        using var master = new XcpMaster(slave, MasterOptions(), _time);
        var scheduler = NewScheduler(master, Map(Entry("Padded", byteLength: 2, physical: 0x10)));

        var result = await scheduler.PollOnceAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(PollingCycleOutcome.Executed, result.Outcome);
        Assert.Equal(new byte[] { 0xA1, 0xB2 }, Assert.Single(result.Values).Data);
        Assert.Empty(result.Failures);

        // 从机确实回了 8B DLC（FF + 2B 数据 + 5B padding）。
        var response = Assert.Single(slave.Received);
        Assert.Equal(8, response.Length);
        Assert.Equal(0xFF, response[0]);
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
        var scheduler = NewScheduler(master, Map(Entry("Object", byteLength, address)));

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
            () => NewScheduler(master, Map(entry)));
        Assert.Contains("Untranslated", ex.Message);
        Assert.Empty(slave.Sent); // 线上零流量
    }

    // ---- (F5) fail-loud：物理地址超 32 位线上地址空间 ----
    [Fact]
    public void Ctor_fails_loud_when_physical_address_exceeds_32bit_wire_space()
    {
        var slave = new ScriptedPollingSlave(new byte[] { 0x01 });
        using var master = new XcpMaster(slave, MasterOptions(), _time);
        var entry = Entry("Beyond32Bit", byteLength: 8, physical: 0x1_0000_0000);

        var ex = Assert.Throws<InvalidOperationException>(() => NewScheduler(master, Map(entry)));
        Assert.Contains("Beyond32Bit", ex.Message);
        Assert.Contains("32-bit", ex.Message);
        Assert.Empty(slave.Sent);
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
        var scheduler = NewScheduler(
            master,
            Map(Entry("Small", byteLength: 1, physical: 0x10)),
            new PollingSchedulerOptions { Period = period });
        Assert.Equal(period, scheduler.Period);

        var results = new ConcurrentQueue<PollingCycleResult>();
        var cycleCount = 0;
        using var cts = new CancellationTokenSource();
        var run = scheduler.RunAsync(r => { results.Enqueue(r); Interlocked.Increment(ref cycleCount); }, cts.Token);

        Assert.True(results.IsEmpty); // 首拍前不轮询（等待先行）

        _time.Advance(period);
        await AdvanceUntil(() => Volatile.Read(ref cycleCount) >= 1);
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
        var scheduler = NewScheduler(
            master, Map(Entry("Obj", 8, 0x1000)), rotationGate: gate);

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
        var scheduler = NewScheduler(
            master,
            Map(Entry("Small", byteLength: 1, physical: 0x10)),
            new PollingSchedulerOptions { Period = period },
            rotationGate: gate);

        var results = new ConcurrentQueue<PollingCycleResult>();
        var cycleCount = 0;
        using var cts = new CancellationTokenSource();
        var run = scheduler.RunAsync(r => { results.Enqueue(r); Interlocked.Increment(ref cycleCount); }, cts.Token);

        _time.Advance(period);
        await AdvanceUntil(() => Volatile.Read(ref cycleCount) >= 1);
        Assert.Equal(PollingCycleOutcome.SkippedRotation, results.First().Outcome);
        Assert.Empty(slave.Sent);

        gate.InRotation = false;
        _time.Advance(period);
        await AdvanceUntil(() => Volatile.Read(ref cycleCount) >= 2);

        Assert.Single(slave.Sent); // 只有开门后的那一拍上了总线
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    // ---- (F3) 负响应：归因出站、不插 quiesce（带内应答零时钟推进）、下一拍自愈 ----
    [Fact]
    public async Task Negative_response_is_attributed_without_quiesce_and_next_cycle_recovers()
    {
        var slave = new ScriptedPollingSlave(new byte[] { 0x2A });
        slave.FailShortUpload(0x10); // 条目 A 恒负响应
        using var master = new XcpMaster(slave, MasterOptions(), _time);
        var scheduler = NewScheduler(master, Map(
            Entry("Neg", byteLength: 1, physical: 0x10),
            Entry("Ok1", byteLength: 1, physical: 0x12),
            Entry("Ok2", byteLength: 1, physical: 0x14)));

        var before = _time.GetUtcNow();
        var result = await scheduler.PollOnceAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(before, _time.GetUtcNow()); // 负响应不插 quiesce（零时钟推进快路径）

        Assert.Equal(2, result.Values.Count);
        Assert.Equal("Ok1|Ok2", string.Join("|", result.Values.Select(v => v.ObjectName)));
        var failure = Assert.Single(result.Failures);
        Assert.Equal("Neg", failure.ObjectName);
        Assert.Equal(PollingFailureKind.NegativeResponse, failure.Kind);
        Assert.NotNull(failure.ErrorCode);
        Assert.Equal(1, scheduler.ConsecutiveFailedCycles);

        // 解除负响应 → 下一拍全量恢复 + 计数归零。
        slave.ClearShortUploadFailures();
        var recovered = await scheduler.PollOnceAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(recovered.Failures);
        Assert.Equal(3, recovered.Values.Count);
        Assert.Equal(0, scheduler.ConsecutiveFailedCycles);
    }

    // ---- (F3) 超时：归因出站 + 同拍下一命令前 quiesce ≥T1（时钟断言）----
    [Fact]
    public async Task Timeout_is_attributed_and_next_command_waits_quiesce_of_at_least_t1()
    {
        var slave = new ScriptedPollingSlave(new byte[] { 0x2A });
        slave.Clock = () => _time.GetUtcNow();
        slave.SilenceShortUpload(0x10); // 条目 A 两次 attempt 都无应答 → T1 超时 ×2
        using var master = new XcpMaster(slave, MasterOptions(), _time);
        var scheduler = NewScheduler(master, Map(
            Entry("Silent", byteLength: 1, physical: 0x10),
            Entry("Ok", byteLength: 1, physical: 0x12)));

        var before = _time.GetUtcNow();
        var poll = scheduler.PollOnceAsync();
        await AdvanceUntil(() => poll.IsCompleted, step: TimeSpan.FromMilliseconds(100));
        var result = await poll;

        var after = _time.GetUtcNow();
        Assert.True(after - before >= 3 * T1, $"fake clock advanced {after - before}, expected >= 3×T1 " +
                                               "(2 timeout attempts + quiesce).");

        var failure = Assert.Single(result.Failures);
        Assert.Equal("Silent", failure.ObjectName);
        Assert.Equal(PollingFailureKind.Timeout, failure.Kind);
        Assert.Equal(new byte[] { 0x2A }, Assert.Single(result.Values).Data);

        // 帧序列：A 的两次 attempt（同一 SHORT_UPLOAD 重发）+ B 一次。
        Assert.Equal(3, slave.Sent.Count);
        Assert.Equal(0x10u, ShortUploadAddress(slave.Sent[0]));
        Assert.Equal(0x10u, ShortUploadAddress(slave.Sent[1]));
        Assert.Equal(0x12u, ShortUploadAddress(slave.Sent[2]));

        // quiesce 时钟断言：A 最后一次 attempt 与 B 命令之间 ≥T1（同拍内下一命令前）。
        var quiesceGap = slave.SentAt[2] - slave.SentAt[1];
        Assert.True(quiesceGap >= T1, $"gap before next command was {quiesceGap}, expected >= T1.");

        // (R2) 拍终 quiesce 专属断言：最后一帧与本拍返回之间 ≥T1
        //（timeoutSeen → 返回前再插 ≥T1，下一拍入口/调用方侧命令皆安全）。
        var tailGap = after - slave.SentAt[^1];
        Assert.True(tailGap >= T1, $"gap between last frame and cycle return was {tailGap}, expected >= T1.");
    }

    // ---- (F3) RunAsync 存活：一次失败拍不杀死循环，连续失败计数可被 T13 观察 ----
    [Fact]
    public async Task RunAsync_survives_failing_cycles_and_tracks_consecutive_failures()
    {
        var period = TimeSpan.FromMilliseconds(250);
        var slave = new ScriptedPollingSlave(new byte[] { 0x2A });
        slave.FailShortUpload(0x10);
        using var master = new XcpMaster(slave, MasterOptions(), _time);
        var scheduler = NewScheduler(
            master,
            Map(Entry("Neg", byteLength: 1, physical: 0x10)),
            new PollingSchedulerOptions { Period = period });

        var results = new ConcurrentQueue<PollingCycleResult>();
        var cycleCount = 0;
        using var cts = new CancellationTokenSource();
        var run = scheduler.RunAsync(r => { results.Enqueue(r); Interlocked.Increment(ref cycleCount); }, cts.Token);

        _time.Advance(period);
        await AdvanceUntil(() => Volatile.Read(ref cycleCount) >= 1);
        _time.Advance(period);
        await AdvanceUntil(() => Volatile.Read(ref cycleCount) >= 2);

        Assert.False(run.IsFaulted); // 失败拍不杀死 RunAsync
        Assert.All(results, r => Assert.Single(r.Failures));
        Assert.Equal(2, scheduler.ConsecutiveFailedCycles);

        slave.ClearShortUploadFailures();
        _time.Advance(period);
        await AdvanceUntil(() => Volatile.Read(ref cycleCount) >= 3);
        Assert.Empty(results.Last().Failures);
        Assert.Equal(0, scheduler.ConsecutiveFailedCycles);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    // ---- 单飞：与 RotationScheduler 同型（XcpMaster 单发单收，排队即串扰）----
    [Fact]
    public async Task PollOnce_rejects_concurrent_invocation_while_previous_cycle_pending()
    {
        var slave = new ScriptedPollingSlave(new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 });
        using var master = new XcpMaster(slave, MasterOptions(), _time);
        var scheduler = NewScheduler(master, Map(Entry("Big", 8, 0x1000)));

        slave.HoldUpload();
        var first = scheduler.PollOnceAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => scheduler.PollOnceAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("single-flight", ex.Message);

        slave.ReleaseHold();
        var result = await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 }, Assert.Single(result.Values).Data);
    }

    // ---- (F4) 静默握手：在途拍挂住时 WaitForQuietAsync 不完成，释放后完成 ----
    [Fact]
    public async Task WaitForQuietAsync_completes_only_after_inflight_cycle_drains()
    {
        var slave = new ScriptedPollingSlave(new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 });
        using var master = new XcpMaster(slave, MasterOptions(), _time);
        var scheduler = NewScheduler(master, Map(Entry("Big", 8, 0x1000)));

        slave.HoldUpload();
        var cycle = scheduler.PollOnceAsync();

        var quiet = scheduler.WaitForQuietAsync();
        await Task.Delay(50); // 在途拍被门闩挂住 → 握手不得完成
        Assert.False(quiet.IsCompleted);

        slave.ReleaseHold();
        await Task.WhenAll(cycle, quiet).WaitAsync(TimeSpan.FromSeconds(5));

        // 闩已归还：后续拍可正常开跑（gate 复用无泄漏）。
        var next = await scheduler.PollOnceAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PollingCycleOutcome.Executed, next.Outcome);
    }

    // ---- (R1) 输赢分支反向钉死：拍先过闩、gate 后置位 → 握手被在途拍压住 ----
    // 顺序不变式：抢闩严格先于 gate 检查。gate 检查通过后才置位 gate 的在途拍
    // 必须压住 WaitForQuietAsync（否则握手放行轮转，在途拍会在轮转期上线）。
    [Fact]
    public async Task WaitForQuietAsync_is_held_by_inflight_cycle_that_passed_gate_before_it_was_set()
    {
        var gate = new TestRotationGate { InRotation = false };
        var slave = new ScriptedPollingSlave(new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 });
        using var master = new XcpMaster(slave, MasterOptions(), _time);
        var scheduler = NewScheduler(master, Map(Entry("Big", 8, 0x1000)), rotationGate: gate);

        slave.HoldUpload(); // 拍抢到闩、通过 gate 检查（当时 gate 开），随后挂在 UPLOAD 上
        var cycle = scheduler.PollOnceAsync();

        gate.InRotation = true; // 拍已在途后才置位
        var quiet = scheduler.WaitForQuietAsync();
        await Task.Delay(50);
        Assert.False(quiet.IsCompleted); // 在途拍压住握手

        slave.ReleaseHold();
        await Task.WhenAll(cycle, quiet).WaitAsync(TimeSpan.FromSeconds(5));
    }

    // ---- 空轮询集合：零线上流量 ----
    [Fact]
    public async Task PollOnce_with_no_polling_entries_produces_no_traffic()
    {
        var slave = new ScriptedPollingSlave(Array.Empty<byte>());
        using var master = new XcpMaster(slave, MasterOptions(), _time);
        var scheduler = NewScheduler(
            master,
            new PlannedAcquisitionMap(0, 0, Array.Empty<PlannedOdt>(), Array.Empty<PlannedPollingEntry>()));

        var result = await scheduler.PollOnceAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(PollingCycleOutcome.NoEntries, result.Outcome);
        Assert.Empty(slave.Sent);
    }

    // ---- helpers ----

    private static XcpMasterOptions MasterOptions() => new(MasterCanId);

    private PollingScheduler NewScheduler(
        XcpMaster master,
        PlannedAcquisitionMap map,
        PollingSchedulerOptions? options = null,
        IXcpRotationGate? rotationGate = null) =>
        new(master, MasterOptions(), map, options, rotationGate, _time);

    private static PlannedPollingEntry Entry(string name, int byteLength, ulong physical) =>
        new(name, 0, byteLength, 0, physical, physical, PlannedPollingCause.ObjectTooLarge);

    private static PlannedAcquisitionMap Map(params PlannedPollingEntry[] entries) =>
        new(0, 0, Array.Empty<PlannedOdt>(), entries);

    private static uint ShortUploadAddress(byte[] cmd) =>
        cmd[5] | ((uint)cmd[6] << 8) | ((uint)cmd[7] << 16);

    private async Task AdvanceUntil(Func<bool> condition, TimeSpan? step = null, int timeoutMs = 15000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
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
    /// 脚本化轮询从机：UPLOAD/SHORT_UPLOAD 按配置的载荷吐数据（SHORT_UPLOAD
    /// 自包含恒读载荷起点；SET_MTA 重置游标、MTA 随 UPLOAD 后自增——XCP 标准）。
    /// 支持按地址负响应/静默（T12 review F3 用例）与固定 8B DLC padding（F2 用例）。
    /// </summary>
    private sealed class ScriptedPollingSlave : IXcpTransport
    {
        private readonly byte[] _payload;
        private readonly HashSet<uint> _negativeShortUpload = new();
        private readonly HashSet<uint> _silentShortUpload = new();
        private int _cursor;
        private uint _mta;
        private TaskCompletionSource<object?>? _hold;

        public ScriptedPollingSlave(byte[] payload) => _payload = payload;

        public event Action<CanFrame>? FrameReceived;

        public long FramesDropped => 0;

        public List<byte[]> Sent { get; } = new();

        public List<byte[]> Received { get; } = new();

        public List<DateTimeOffset> SentAt { get; } = new();

        public Func<DateTimeOffset>? Clock { get; set; }

        /// <summary>正响应固定 8B DLC（数据后补零 padding）——F2 宽容切片用例。</summary>
        public bool PadUploadResponses;

        public void FailShortUpload(uint address) => _negativeShortUpload.Add(address);

        public void SilenceShortUpload(uint address) => _silentShortUpload.Add(address);

        public void ClearShortUploadFailures()
        {
            _negativeShortUpload.Clear();
            _silentShortUpload.Clear();
        }

        public void HoldUpload() =>
            _hold = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseHold() => _hold?.TrySetResult(null);

        public async ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
        {
            var cmd = frame.Data.ToArray();
            Sent.Add(cmd);
            SentAt.Add(Clock?.Invoke() ?? DateTimeOffset.MinValue);
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
                    if (cmd[0] == XcpPid.ShortUpload)
                    {
                        var address = ShortUploadAddress(cmd);
                        if (_silentShortUpload.Contains(address))
                            return; // 静默：master 两次 attempt 都无应答才超时
                        if (_negativeShortUpload.Contains(address))
                        {
                            Negative(XcpError.AccessDenied);
                            return;
                        }
                        Emit(_payload.Take(count).ToArray(), pad: PadUploadResponses);
                        return;
                    }

                    // UPLOAD：MTA 连续分块。
                    var chunk = new byte[count];
                    Array.Copy(_payload, _cursor, chunk, 0, count);
                    _cursor += count;
                    Emit(chunk, pad: PadUploadResponses);
                    break;
                }
                default:
                    Positive();
                    break;
            }
        }

        private void Positive() => Emit(Array.Empty<byte>(), pad: false);

        private void Negative(XcpError error) =>
            FrameReceived?.Invoke(Frame(new byte[] { XcpPid.Error, (byte)error }));

        private void Emit(byte[] data, bool pad)
        {
            // pad = 固定 8B DLC（DLC 对齐 padding），S2 主站侧必须宽容切片（F2）。
            var length = pad ? 8 : 1 + data.Length;
            var frameBytes = new byte[length];
            frameBytes[0] = XcpPid.PositiveResponse;
            data.CopyTo(frameBytes, 1);
            Received.Add(frameBytes);
            FrameReceived?.Invoke(Frame(frameBytes));
        }

        private static CanFrame Frame(byte[] data) => new(
            new CanId(0x18FFF666, FrameFormat.Extended),
            new ReadOnlyMemory<byte>(data),
            FrameFlags.None,
            ChannelId.None,
            default);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static uint ReadU32(byte[] frame, int offset) =>
            frame[offset] | ((uint)frame[offset + 1] << 8) | ((uint)frame[offset + 2] << 16) | ((uint)frame[offset + 3] << 24);
    }
}
