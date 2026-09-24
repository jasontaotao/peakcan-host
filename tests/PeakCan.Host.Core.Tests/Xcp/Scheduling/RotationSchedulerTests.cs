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
/// 换表计划空窗通知挂点（生产端 T15 接线）。
/// </summary>
public class RotationSchedulerTests
{
    private static readonly CanId MasterCanId = new(0x18FFF667, FrameFormat.Extended);

    private readonly FakeTimeProvider _time = new();

    // ---- (a) 15 ODT 分批轮转顺序：stop → 逐 ODT 升序 setptr/write → start ----
    [Fact]
    public async Task Configure_rewrite_orders_odts_ascending_with_pointer_before_each_write()
    {
        var slave = new ScriptedXcpSlave();
        var map = Map15Odts();
        using var master = NewMaster(slave);
        var scheduler = new RotationScheduler(master);

        var result = await scheduler.ConfigureRotationAsync(map).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RotationTableState.Running, result.TableState);
        Assert.Empty(result.RecoveredFailures);

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
        using var master = NewMaster(slave);
        var scheduler = new RotationScheduler(master);

        await scheduler.ConfigureRotationAsync(Map15Odts()).WaitAsync(TimeSpan.FromSeconds(5));

        var sent = slave.Sent;
        var stopIndex = sent.FindIndex(f => f[0] == XcpPid.StartStopDaqList && f[1] == 0x00);
        var firstWriteIndex = sent.FindIndex(f => f[0] == XcpPid.WriteDaq);
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
        using var master = NewMaster(slave);
        var scheduler = new RotationScheduler(master);

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
        using var master = NewMaster(slave);
        var scheduler = new RotationScheduler(master);

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
        using var master = NewMaster(slave);
        var scheduler = new RotationScheduler(master);

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
        using var master = NewMaster(slave);
        var scheduler = new RotationScheduler(master);

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
        using var master = NewMaster(slave);
        var scheduler = new RotationScheduler(master, gapNotifier: notifier);

        await scheduler.ConfigureRotationAsync(Map15Odts()).WaitAsync(TimeSpan.FromSeconds(5));

        var gap = Assert.Single(notifier.Windows);
        Assert.Equal((ushort)15, gap.OdtCount);
        Assert.Equal(91, gap.EntryCount);
        Assert.True(gap.ExpectedMaxDuration > TimeSpan.Zero);
        // 空窗起点 = stop 应答刚落地（线上只有 stop 一帧）且从机侧已停。
        Assert.Equal(1, notifier.SentCounts[0]);
        Assert.False(notifier.RunningFlags[0]);
    }

    // ---- 测试脚手架 ----

    private XcpMaster NewMaster(ScriptedXcpSlave slave) =>
        new(slave, new XcpMasterOptions(MasterCanId), _time);

    /// <summary>15 ODT 满载方案：4B×1 + 2B×3×2 + 1B×7×12，共 91 条目。</summary>
    private static PlannedAcquisitionMap Map15Odts()
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
        return new PlannedAcquisitionMap(0, (ushort)odts.Count, odts, Array.Empty<PlannedPollingEntry>());
    }

    private static uint EntryAddress(ushort odt, ushort entry) => 0x1000u + (uint)(odt * 8 + entry);

    /// <summary>S2 元素指针约定（钉死）：daq&lt;&lt;16 | odt&lt;&lt;8 | entry，本测试 daq=0。</summary>
    private static uint ElementPointer(ushort odt, ushort entry) => (uint)((odt << 8) | entry);

    private static uint ReadU32(byte[] frame, int offset) =>
        frame[offset] | ((uint)frame[offset + 1] << 8) | ((uint)frame[offset + 2] << 16) | ((uint)frame[offset + 3] << 24);

    /// <summary>
    /// 脚本化模拟从机：状态机强制"表运行中 WRITE_DAQ 必负响应"（spec §1 顺序写死的
    /// 从机侧判据）；响应在 WriteAsync 内同步派发（XcpVirtualSlave 同款简化）。
    /// </summary>
    private sealed class ScriptedXcpSlave : IXcpTransport
    {
        private static readonly CanId SlaveCanId = new(0x18FFF666, FrameFormat.Extended);

        private readonly HashSet<int> _failWriteAt = new();
        private int _writeCount;

        public List<byte[]> Sent { get; } = new();

        public bool Running { get; private set; }

        public int NegativesSent { get; private set; }

        public event Action<CanFrame>? FrameReceived;

        public long FramesDropped => 0;

        /// <summary>第 index 条（1 起）WRITE_DAQ 负响应一次。</summary>
        public void FailWriteDaqOnce(int index) => _failWriteAt.Add(index);

        /// <summary>从第 index 条（1 起）起 WRITE_DAQ 全部负响应（恢复耗尽路径）。</summary>
        public void FailWriteDaqFrom(int firstIndex) => _failWriteFrom = firstIndex;

        private int _failWriteFrom = int.MaxValue;

        public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
        {
            var bytes = frame.Data.ToArray();
            Sent.Add(bytes);
            Respond(bytes);
            return ValueTask.FromResult(Result<Unit>.Ok(default));
        }

        private void Respond(byte[] cmd)
        {
            switch (cmd[0])
            {
                case XcpPid.StartStopDaqList:
                    Running = cmd[1] == 0x01;
                    Positive();
                    break;
                case XcpPid.WriteDaq:
                    _writeCount++;
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