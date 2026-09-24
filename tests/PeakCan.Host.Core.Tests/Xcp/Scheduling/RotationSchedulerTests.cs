using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using PeakCan.HIL.Core;
using PeakCan.Host.Core.Xcp.Abstractions;
using PeakCan.Host.Core.Xcp.Protocol;
using PeakCan.Host.Core.Xcp.Scheduling;

namespace PeakCan.Host.Core.Tests.Xcp.Scheduling;

/// <summary>
/// S2-T11 RotationScheduler（spec §1 轮转形状 + §3 Scheduling）：
/// stop → SET_DAQ_PTR/WRITE_DAQ 重写 → start 的顺序写死状态机、
/// mode 0/1 直控 list 0、stop 后重写失败的归因与可注入恢复（重试重写/整表重建）、
/// 超时后 quiesce 间隙（T4 评审裁决）、stop/start 阶段失败归因、
/// 换表计划空窗通知挂点（生产端 T15 接线）。
/// </summary>
public class RotationSchedulerTests
{
    private static readonly CanId MasterCanId = new(0x18FFF667, FrameFormat.Extended);

    /// <summary>T1（XcpMasterOptions.DefaultTimeout）——quiesce 间隙与超时测试的钟源基准。</summary>
    private static readonly TimeSpan T1 = TimeSpan.FromMilliseconds(2000);

    private readonly FakeTimeProvider _time = new();

    // ---- (a) 15 ODT 分批轮转顺序：stop → 逐 ODT 升序 setptr/write → start ----
    [Fact]
    public async Task Configure_rewrite_orders_odts_ascending_with_pointer_before_each_write()
    {
        var slave = new ScriptedXcpSlave();
        var map = Map15Odts();
        var (master, scheduler) = NewSession(slave);
        using var _ = master;

        var before = _time.GetUtcNow(); // 无超时路径不得推进时钟（quiesce 零延迟快路径）
        var result = await scheduler.ConfigureRotationAsync(map).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RotationTableState.Running, result.TableState);
        Assert.Empty(result.RecoveredFailures);
        Assert.Equal(before, _time.GetUtcNow());

        var sent = slave.Sent;
        Assert.Equal(XcpPid.StartStopDaqList, sent[0][0]);
        Assert.Equal(0x00, sent[0][1]); // mode = stop
        Assert.Equal(0x00, sent[0][2]); // daq list 0

        // 序列逐帧比对：每条目前先 SET_DAQ_PTR 指到 (ODT, Entry)，再 WRITE_DAQ 定义条目。
        var i = 1;
        foreach (var odt in map.Odts)
        {
            foreach (var e in odt.Entries)
            {
                var setptr = sent[i++];
                Assert.Equal(XcpPid.SetDaqPtr, setptr[0]);
                Assert.Equal(0x00, setptr[3]); // address extension 0（ADDR_EXT free 基线）
                Assert.Equal(ElementPointer(odt.OdtIndex, e.EntryIndex), ReadU32(setptr, 4));

                var write = sent[i++];
                Assert.Equal(XcpPid.WriteDaq, write[0]);
                Assert.Equal(0x00, write[1]); // bit offset 0（spec §1 无位偏置）
                Assert.Equal((byte)e.ByteLength, write[2]);
                Assert.Equal(0x00, write[3]);
                Assert.Equal(EntryAddress(odt.OdtIndex, e.EntryIndex), ReadU32(write, 4));
            }
        }

        Assert.Equal(XcpPid.StartStopDaqList, sent[i][0]);
        Assert.Equal(0x01, sent[i][1]); // mode = start
        Assert.Equal(0x00, sent[i][2]);
        Assert.Equal(sent.Count, i + 1);
    }

    // ---- (b) WRITE_DAQ 必须发生在 stop 之后（序列断言 + 从机运行态拒绝双重证明）----
    [Fact]
    public async Task WriteDaq_never_precedes_stop_even_with_running_state_enforcement()
    {
        var slave = new ScriptedXcpSlave();
        var (master, scheduler) = NewSession(slave);
        using var _ = master;

        await scheduler.ConfigureRotationAsync(Map15Odts()).WaitAsync(TimeSpan.FromSeconds(5));

        var sent = slave.Sent;
        var stopIndex = sent.FindIndex(f => f[0] == XcpPid.StartStopDaqList && f[1] == 0x00);
        var firstWriteIndex = sent.FindIndex(f => f[0] == XcpPid.WriteDaq);
        Assert.NotEqual(-1, stopIndex);
        Assert.NotEqual(-1, firstWriteIndex);
        Assert.True(firstWriteIndex > stopIndex, "WRITE_DAQ must only be issued after START_STOP_DAQ_LIST(stop).");

        // 模拟从机钉死：表运行中收到 WRITE_DAQ 必回负响应——全程零负响应
        // 即状态机从未产生"运行中重写"序列。
        Assert.Equal(0, slave.NegativesSent);
    }

    // ---- (c) 仅 mode 0/1 直控 list 0；mode 2/START_STOP_SYNCH 组合路径不出现 ----
    [Fact]
    public async Task Only_mode0_and_mode1_directly_control_daq_list0_and_synch_is_never_sent()
    {
        var slave = new ScriptedXcpSlave();
        var (master, scheduler) = NewSession(slave);
        using var _ = master;

        await scheduler.ConfigureRotationAsync(Map15Odts()).WaitAsync(TimeSpan.FromSeconds(5));

        var sent = slave.Sent;
        Assert.DoesNotContain(sent, f => f[0] == XcpPid.StartStopSynch);
        var listCommands = sent.Where(f => f[0] == XcpPid.StartStopDaqList).ToList();
        Assert.Equal(2, listCommands.Count);
        Assert.All(listCommands, f =>
        {
            Assert.InRange(f[1], 0x00, 0x01); // mode 只有 stop(0)/start(1)
            Assert.Equal(0x00, f[2]);         // 直控 DAQ list 0
        });
        Assert.Equal(0x00, listCommands[0][1]); // 先 stop
        Assert.Equal(0x01, listCommands[1][1]); // 后 start
    }

    // ---- (d) 恢复路径一：重试重写（原位重发失败条目）----
    [Fact]
    public async Task Rewrite_negative_response_recovers_by_retrying_the_failing_entry()
    {
        var slave = new ScriptedXcpSlave();
        slave.FailWriteDaqOnce(4); // 第 4 条 WRITE_DAQ（ODT1 entry2）负响应一次
        var (master, scheduler) = NewSession(slave);
        using var _ = master;

        var result = await scheduler.ConfigureRotationAsync(
            Map15Odts(),
            new FixedRotationRecoveryPolicy(RotationRecoveryAction.RetryRewrite)).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RotationTableState.Running, result.TableState);
        var failure = Assert.Single(result.RecoveredFailures);
        Assert.Equal(RotationPhase.Rewriting, failure.Phase);
        Assert.Equal((ushort)1, failure.OdtIndex);
        Assert.Equal((ushort)2, failure.EntryIndex);
        Assert.Equal(XcpError.WriteProtected, failure.ErrorCode);

        var sent = slave.Sent;
        Assert.Equal(1, slave.NegativesSent);
        // 整轮只有一对 stop/start：失败与恢复期间表恒处 stop 态，无二次 stop。
        Assert.Equal(2, sent.Count(f => f[0] == XcpPid.StartStopDaqList));
        // 重试重写 = 原位重发失败条目：指针 (1<<8)|2 连续出现两次。
        Assert.Equal(2, sent.Count(f => f[0] == XcpPid.SetDaqPtr && ReadU32(f, 4) == ElementPointer(1, 2)));
        Assert.Equal(0x01, sent[^1][1]); // 最后一条是 start
    }

    // ---- (d) 恢复路径二：整表重建（弃进度、从 ODT0 全量重写）----
    [Fact]
    public async Task Rewrite_negative_response_recovers_by_rebuilding_the_whole_table()
    {
        var slave = new ScriptedXcpSlave();
        slave.FailWriteDaqOnce(4);
        var (master, scheduler) = NewSession(slave);
        using var _ = master;

        var result = await scheduler.ConfigureRotationAsync(
            Map15Odts(),
            new FixedRotationRecoveryPolicy(RotationRecoveryAction.RebuildTable)).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RotationTableState.Running, result.TableState);
        var failure = Assert.Single(result.RecoveredFailures);
        Assert.Equal(RotationPhase.Rewriting, failure.Phase);
        Assert.Equal((ushort)1, failure.OdtIndex);
        Assert.Equal((ushort)2, failure.EntryIndex);

        var sent = slave.Sent;
        // 整表重建：ODT0 的指针 (0<<8)|0 再次出现，失败条目 (1<<8)|2 也重写一次。
        Assert.Equal(2, sent.Count(f => f[0] == XcpPid.SetDaqPtr && ReadU32(f, 4) == ElementPointer(0, 0)));
        Assert.Equal(2, sent.Count(f => f[0] == XcpPid.SetDaqPtr && ReadU32(f, 4) == ElementPointer(1, 2)));
        Assert.Equal(2, sent.Count(f => f[0] == XcpPid.StartStopDaqList));
        Assert.Equal(0x01, sent[^1][1]);
    }

    // ---- (d) 恢复耗尽：归因值 + 表停 stop 态，绝不带半成品条目上线 ----
    [Fact]
    public async Task Rewrite_failure_without_effective_recovery_stays_stopped_and_attributes()
    {
        var slave = new ScriptedXcpSlave();
        slave.FailWriteDaqFrom(4); // 从第 4 条起全部负响应
        var (master, scheduler) = NewSession(slave);
        using var _ = master;

        var ex = await Assert.ThrowsAsync<RotationFailedException>(() =>
            scheduler.ConfigureRotationAsync(
                Map15Odts(),
                new FixedRotationRecoveryPolicy(RotationRecoveryAction.RetryRewrite)).WaitAsync(TimeSpan.FromSeconds(5)));

        // 归因值：失败条目 + 错误码 + 表停 stop 态。
        Assert.Equal(RotationTableState.Stopped, ex.TableState);
        Assert.Equal(RotationTableState.Stopped, scheduler.TableState);
        Assert.Equal(RotationPhase.Rewriting, ex.Failure.Phase);
        Assert.Equal((ushort)1, ex.Failure.OdtIndex);
        Assert.Equal((ushort)2, ex.Failure.EntryIndex);
        Assert.Equal(XcpError.WriteProtected, ex.Failure.ErrorCode);

        // 未发 START：恢复耗尽后表绝不启动。
        Assert.DoesNotContain(slave.Sent, f => f[0] == XcpPid.StartStopDaqList && f[1] == 0x01);
        Assert.False(slave.Running);
    }

    // ---- (e) 计划空窗通知挂点：stop 应答后、首条重写前触发（生产端 T15）----
    [Fact]
    public async Task Plan_gap_window_notification_fires_between_stop_and_rewrite()
    {
        var slave = new ScriptedXcpSlave();
        var notifier = new RecordingGapNotifier(slave);
        var (master, scheduler) = NewSession(slave, gapNotifier: notifier);
        using var _ = master;

        await scheduler.ConfigureRotationAsync(Map15Odts()).WaitAsync(TimeSpan.FromSeconds(5));

        var gap = Assert.Single(notifier.Windows);
        Assert.Equal((ushort)15, gap.OdtCount);
        Assert.Equal(91, gap.EntryCount);

        // 真实上界（I2-R 公式，T1=2000ms、MaxRetries=1、恢复上限 D=3、quiesce=T1）：
        //   commands = 1(stop) + (1+D)×2×91(整表重写最坏形态) + 1(start) = 730
        //   命令预算 = 730 × T1×(MaxRetries+1) = 730 × 4000 = 2920000ms
        //   quiesce 预算 = (D+1) × T1 = 4 × 2000 = 8000ms（D 次恢复 + 1 次 START 收尾）
        //   上界 = 2920000 + 8000 = 2928000ms
        Assert.Equal(TimeSpan.FromMilliseconds(2928000), gap.ExpectedMaxDuration);

        // 空窗起点 = stop 应答刚落地（线上只有 stop 一帧）且从机侧已停。
        Assert.Equal(1, notifier.SentCounts[0]);
        Assert.False(notifier.RunningFlags[0]);
    }

    // ---- (C1) 超时后 quiesce：恢复重发前与 START 前都保持 ≥T1 间隙 ----
    [Fact]
    public async Task Timeout_recovery_waits_quiesce_before_retry_and_before_start()
    {
        var slave = new ScriptedXcpSlave();
        slave.SilenceWriteDaqOnce(4); // write#4 两 attempt 都无应答 → master T1 超时
        var (master, scheduler) = NewSession(slave);
        using var _ = master;

        // 帧序：0 stop；1-2 ODT0e0；3-4 ODT1e0；5-6 ODT1e1；7 setptr；8 write#4（attempt1）。
        var rotation = scheduler.ConfigureRotationAsync(
            Map15Odts(), new FixedRotationRecoveryPolicy(RotationRecoveryAction.RetryRewrite));
        await WaitFor(() => slave.Sent.Count >= 9); // attempt1 在途（无响应）

        await AdvanceUntil(() => slave.Sent.Count >= 10, TimeSpan.FromMilliseconds(200)); // master attempt2 重发
        await AdvanceUntil(() => slave.Sent.Count >= 11, TimeSpan.FromMilliseconds(100)); // 超时→quiesce→恢复重发
        // 继续步进假时钟直到轮转完成：恢复后写完全部条目，且 START 前还有一次
        // ≥T1 的 quiesce（重写阶段发生过超时）——都需要假时钟推进。
        await AdvanceUntil(
            () => rotation.IsCompleted,
            TimeSpan.FromMilliseconds(100));
        var result = await rotation; // IsCompleted 已保证立即完成

        Assert.Equal(RotationTableState.Running, result.TableState);
        var failure = Assert.Single(result.RecoveredFailures);
        Assert.Null(failure.ErrorCode); // 超时非负响应，ErrorCode 归因 null

        // 恢复重发前的 quiesce：attempt2 → 下一帧间隔 ≥ T1。
        Assert.True(slave.SentAt[10] - slave.SentAt[9] >= T1, "recovery command must wait >= T1 after timeout");
        // 重写完成 → START 之间的 quiesce：发生过超时 → 最后一帧写 → START 间隔 ≥ T1。
        Assert.True(slave.SentAt[^1] - slave.SentAt[^2] >= T1, "START must wait >= T1 after a rewrite timeout");
    }

    // ---- (I1) stop 失败：归因 Running（状态不迁移）、绝不起表 ----
    [Fact]
    public async Task Stop_failure_attributes_running_state_and_never_starts()
    {
        var slave = new ScriptedXcpSlave();
        slave.FailStopOnce();
        var (master, scheduler) = NewSession(slave);
        using var _ = master;

        var ex = await Assert.ThrowsAsync<RotationFailedException>(() =>
            scheduler.ConfigureRotationAsync(Map15Odts()).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(RotationPhase.Stopping, ex.Failure.Phase);
        Assert.Equal(XcpError.CmdBusy, ex.Failure.ErrorCode);
        Assert.Equal(RotationTableState.Running, ex.TableState); // 归因值：stop 失败，表仍在从机侧运行
        Assert.Equal(RotationTableState.Stopped, scheduler.TableState); // 本实例从未观测到运行态，acked 状态不迁移
        Assert.Single(slave.Sent); // 终态失败：stop 之后再无任何命令
    }

    // ---- (I1) start 失败：归因 Stopped（安全侧）、条目已写齐但表不上线 ----
    [Fact]
    public async Task Start_failure_attributes_stopped_safe_side()
    {
        var slave = new ScriptedXcpSlave();
        slave.FailStartOnce();
        var (master, scheduler) = NewSession(slave);
        using var _ = master;

        var ex = await Assert.ThrowsAsync<RotationFailedException>(() =>
            scheduler.ConfigureRotationAsync(Map15Odts()).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(RotationPhase.Starting, ex.Failure.Phase);
        Assert.Equal(XcpError.CmdBusy, ex.Failure.ErrorCode);
        Assert.Equal(RotationTableState.Stopped, ex.TableState); // 归因值：安全侧 stop 态
        Assert.Equal(RotationTableState.Stopped, scheduler.TableState);
        Assert.False(slave.Running); // start 负响应 → 从机未上线
        // 条目重写已完成：91 条 setptr 都发出；START 只尝试一次（负响应）且从机未上线。
        Assert.Equal(91, slave.Sent.Count(f => f[0] == XcpPid.SetDaqPtr));
        Assert.Single(slave.Sent, f => f[0] == XcpPid.StartStopDaqList && f[1] == 0x01);
        Assert.Equal(XcpError.CmdBusy, slave.LastStartStopNegative);
    }

    // ---- (M2) 单飞：第一轮未完成时第二轮并发调用被拒绝 ----
    [Fact]
    public async Task Concurrent_rotation_is_rejected_as_single_flight()
    {
        var slave = new ScriptedXcpSlave();
        slave.HoldUntilReleased(XcpPid.StartStopDaqList); // 第一轮卡在 stop 应答前
        var (master, scheduler) = NewSession(slave);
        using var _ = master;

        var first = scheduler.ConfigureRotationAsync(Map15Odts());
        await WaitFor(() => slave.Sent.Count >= 1); // 第一轮已发出 stop、pending 中

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scheduler.ConfigureRotationAsync(Map15Odts()));

        slave.ReleaseHold();
        var result = await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RotationTableState.Running, result.TableState);
    }

    // ---- (M3) OdtCount 与 Odts.Count 不一致：线上帧前 fail-loud ----
    [Fact]
    public async Task Odt_count_mismatch_fails_loud_before_any_wire_traffic()
    {
        var slave = new ScriptedXcpSlave();
        var (master, scheduler) = NewSession(slave);
        using var _ = master;

        // Odts 有 15 个，OdtCount 谎报 14。
        var inconsistent = new PlannedAcquisitionMap(0, 14, BuildOdts(), Array.Empty<PlannedPollingEntry>());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scheduler.ConfigureRotationAsync(inconsistent));
        Assert.Empty(slave.Sent); // 钉死：校验在任何线上流量之前
    }

    // ---- 测试脚手架 ----

    private static XcpMasterOptions MasterOptions() => new(MasterCanId);

    private (XcpMaster Master, RotationScheduler Scheduler) NewSession(
        ScriptedXcpSlave slave,
        RotationSchedulerOptions? options = null,
        IXcpPlanGapNotifier? gapNotifier = null)
    {
        slave.Clock = () => _time.GetUtcNow();
        var master = new XcpMaster(slave, MasterOptions(), _time);
        var scheduler = new RotationScheduler(master, MasterOptions(), options, gapNotifier, _time);
        return (master, scheduler);
    }

    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException($"condition not met within {timeoutMs} ms");
            await Task.Delay(10);
        }
    }

    /// <summary>推进 FakeTimeProvider 直至条件满足（步进 + 真实等待让续体跑完，无离散推进竞态）。</summary>
    private async Task AdvanceUntil(Func<bool> condition, TimeSpan step, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException($"fake-time condition not met within {timeoutMs} ms");
            _time.Advance(step);
            await Task.Delay(1);
        }
    }

    /// <summary>构建 15 个 ODT：4B×1 + 2B×3×2 + 1B×7×12，共 91 条目。</summary>
    private static List<PlannedOdt> BuildOdts()
    {
        var odts = new List<PlannedOdt>();
        for (ushort odt = 0; odt < 15; odt++)
        {
            var (size, perOdt) = odt switch
            {
                0 => (4, 1),
                1 or 2 => (2, 3),
                _ => (1, 7),
            };
            var entries = new List<PlannedDaqEntry>();
            for (ushort e = 0; e < perOdt; e++)
            {
                entries.Add(new PlannedDaqEntry(
                    0x50u + odt, odt, e, $"Obj_{odt}_{e}", 0, size, e * size,
                    EntryAddress(odt, e), EntryAddress(odt, e)));
            }
            odts.Add(new PlannedOdt(odt, 0x50u + odt, size, entries));
        }
        return odts;
    }

    private static PlannedAcquisitionMap Map15Odts() =>
        new(0, 15, BuildOdts(), Array.Empty<PlannedPollingEntry>());

    private static uint EntryAddress(ushort odt, ushort entry) => 0x1000u + (uint)(odt * 8 + entry);

    /// <summary>S2 元素指针约定（钉死）：daq&lt;&lt;16 | odt&lt;&lt;8 | entry，本测试 daq=0。</summary>
    private static uint ElementPointer(ushort odt, ushort entry) => (uint)((odt << 8) | entry);

    private static uint ReadU32(byte[] frame, int offset) =>
        frame[offset] | ((uint)frame[offset + 1] << 8) | ((uint)frame[offset + 2] << 16) | ((uint)frame[offset + 3] << 24);

    /// <summary>
    /// 脚本化模拟从机：状态机强制"表运行中 WRITE_DAQ 必负响应"（spec §1 顺序写死的
    /// 从机侧判据）；响应在 WriteAsync 内派发（XcpVirtualSlave 同款简化）；
    /// Clock 注入 FakeTimeProvider 时刻，用于断言 quiesce 间隙。
    /// </summary>
    private sealed class ScriptedXcpSlave : IXcpTransport
    {
        private static readonly CanId SlaveCanId = new(0x18FFF666, FrameFormat.Extended);

        private readonly HashSet<int> _failWriteAt = new();
        private readonly List<DateTimeOffset> _sentAt = new();
        private int _writeCount;
        private int _failWriteFrom = int.MaxValue;
        private int _silenceWriteAt = -1;
        private bool _silencingWrite;
        private XcpError? _lastStartStopNegative;
        private bool _failStopOnce;
        private bool _failStartOnce;
        private TaskCompletionSource<object?>? _holdGate;
        private byte _holdPid;

        public List<byte[]> Sent { get; } = new();

        public IReadOnlyList<DateTimeOffset> SentAt => _sentAt;

        public Func<DateTimeOffset>? Clock { private get; set; }

        public bool Running { get; private set; }

        public int NegativesSent { get; private set; }

        /// <summary>最近一次 START_STOP 负响应的错误码（start 失败测试用）。</summary>
        public XcpError? LastStartStopNegative => _lastStartStopNegative;

        public event Action<CanFrame>? FrameReceived;

        public long FramesDropped => 0;

        /// <summary>第 index 条（1 起）WRITE_DAQ 负响应一次。</summary>
        public void FailWriteDaqOnce(int index) => _failWriteAt.Add(index);

        /// <summary>从第 index 条（1 起）起 WRITE_DAQ 全部负响应（恢复耗尽路径）。</summary>
        public void FailWriteDaqFrom(int firstIndex) => _failWriteFrom = firstIndex;

        /// <summary>第 index 条（1 起）WRITE_DAQ 首次无应答（master T1 超时路径）。</summary>
        public void SilenceWriteDaqOnce(int index) => _silenceWriteAt = index;

        /// <summary>下一次 START_STOP_DAQ_LIST(stop) 负响应（I1 归因测试）。</summary>
        public void FailStopOnce() => _failStopOnce = true;

        /// <summary>下一次 START_STOP_DAQ_LIST(start) 负响应（I1 归因测试）。</summary>
        public void FailStartOnce() => _failStartOnce = true;

        /// <summary>指定 PID 的下一帧挂起不响应，直到 ReleaseHold（单飞测试）。</summary>
        public void HoldUntilReleased(byte pid)
        {
            _holdPid = pid;
            _holdGate = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void ReleaseHold() => _holdGate?.TrySetResult(null);

        public async ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
        {
            var bytes = frame.Data.ToArray();
            Sent.Add(bytes);
            _sentAt.Add(Clock?.Invoke() ?? DateTimeOffset.MinValue);
            if (_holdGate is not null && bytes[0] == _holdPid)
                await _holdGate.Task.ConfigureAwait(false);
            Respond(bytes);
            return Result<Unit>.Ok(default);
        }

        private void Respond(byte[] cmd)
        {
            // 静默窗只针对同一条 WRITE_DAQ 命令的连续重发；
            // 任何其他命令（恢复重发的 SET_DAQ_PTR / START_STOP）到达即解除。
            if (cmd[0] != XcpPid.WriteDaq)
                _silencingWrite = false;

            switch (cmd[0])
            {
                case XcpPid.StartStopDaqList:
                    var isStop = cmd[1] == 0x00;
                    var failList = isStop ? _failStopOnce : _failStartOnce;
                    if (failList) _lastStartStopNegative = XcpError.CmdBusy;
                    if (failList)
                    {
                        if (isStop) _failStopOnce = false; else _failStartOnce = false;
                        Negative(XcpError.CmdBusy);
                    }
                    else
                    {
                        Running = !isStop; // 只有正应答才迁移从机运行态
                        Positive();
                    }
                    break;
                case XcpPid.WriteDaq:
                    _writeCount++;
                    if (_silencingWrite || _writeCount == _silenceWriteAt)
                    {
                        // 静默覆盖同一条命令的连续重发（master 两次 attempt 都无应答才超时）；
                        // 一旦出现非 WRITE_DAQ 命令（恢复重发的 setptr）即解除静默。
                        _silencingWrite = true;
                        _silenceWriteAt = -1;
                        return;
                    }
                    if (Running || _writeCount >= _failWriteFrom || _failWriteAt.Remove(_writeCount))
                    {
                        NegativesSent++;
                        Negative(XcpError.WriteProtected);
                    }
                    else
                    {
                        Positive();
                    }
                    break;
                default:
                    Positive();
                    break;
            }
        }

        private void Positive() => Emit(XcpPid.PositiveResponse);

        private void Negative(XcpError error) => Emit(XcpPid.Error, (byte)error);

        private void Emit(params byte[] data) => FrameReceived?.Invoke(new CanFrame(
            SlaveCanId,
            new ReadOnlyMemory<byte>(data),
            FrameFlags.None,
            ChannelId.None,
            default));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>记录空窗通知到达时刻的从机状态/线上帧数（挂点时序断言）。</summary>
    private sealed class RecordingGapNotifier(ScriptedXcpSlave slave) : IXcpPlanGapNotifier
    {
        public List<RotationGapWindow> Windows { get; } = new();

        public List<int> SentCounts { get; } = new();

        public List<bool> RunningFlags { get; } = new();

        public void OnPlanGapWindow(RotationGapWindow window)
        {
            Windows.Add(window);
            SentCounts.Add(slave.Sent.Count);
            RunningFlags.Add(slave.Running);
        }
    }
}