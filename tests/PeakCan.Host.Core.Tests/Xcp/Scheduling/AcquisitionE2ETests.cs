using System.Text.Json;
using A2lEditor.Core;
using A2lEditor.Core.IfData;
using A2lEditor.Core.Layout;
using A2lEditor.Core.Model;
using PeakCan.HIL.Core;
using PeakCan.Host.Core.Tests.Xcp.TestKit;
using PeakCan.Host.Core.Xcp.Abstractions;
using PeakCan.Host.Core.Xcp.Protocol;
using PeakCan.Host.Core.Xcp.Scheduling;

namespace PeakCan.Host.Core.Tests.Xcp.Scheduling;

/// <summary>
/// S2-T13 调度层模拟从机端到端（组合根 XcpAcquisitionSession）：
/// <list type="bullet">
/// <item>(a) 真机 A2L（TestData/App_merge_INCA.a2l，965 测量输入）→ 规划 → 轮转 →
/// 采集 N 拍：确定性方案 + 105 B/拍节奏（DAQ 侧每拍字节上界）。</item>
/// <item>(b) 溢出降级路径端到端：轮询兜底量真实发生在模拟从机上（缩小夹具）。</item>
/// <item>(c) 轮转失败恢复端到端：stop-后-重写负响应 → RotationFailure 归因 +
/// 恢复动作真实发生（模拟从机帧序列验证）。</item>
/// <item>(d) 采集覆盖断言基于 planner 自产方案 + 包侧 MissingCause——不经
/// IMapAlignmentService——本文件不引用任何 MAP 对账服务命名空间。</item>
/// <item>(e) DOWNLOAD 禁用断言：XcpTransportSpy 记录的全部发送帧中无 0xF0 命令码。</item>
/// </list>
/// 夹具说明（真机 A2L 的已知事实，T13 探针实证）：样例 A2L 的 MEMORY_SEGMENT 只声明
/// CALROM 一段映射（0x20015F00 起长 0x8000），965 个测量的 RAM 地址
///（0x1FFE36C0..0x20008A8D）全部无映射——planner/T11/T12 的 fail-loud（未翻译地址
/// 拒绝上线）是正确行为，测试不得绕过。全流程用例用「真机 A2L + 测试侧补 RAM 映射段」
/// 夹具：965 测量输入 100% 来自真机文件，仅补齐台架本应具备的段映射表。
/// </summary>
public class AcquisitionE2ETests
{
    // ---- (a) 真机 965 测量全流程：规划 → 轮转 → 采集 N 拍 ----

    [Fact]
    public async Task RealSample_FullFlow_Plans_Rotates_And_Acquires_Beats()
    {
        var flow = await RunRealSampleFullFlowAsync();
        var map = flow.Map;

        // 确定性方案已在 RunRealSampleFullFlowAsync 内断言（两次规划逐字节一致）。

        // 105 B/拍节奏：15 ODT、每拍打包字节 ≤ 15×7=105（真机方案 15×4B=60B）。
        var perBeatBytes = map.Odts.SelectMany(o => o.Entries).Sum(e => e.ByteLength);
        Assert.Equal(15, map.Odts.Count);
        Assert.Equal(60, perBeatBytes);
        Assert.True(perBeatBytes <= AcquisitionPlanner.MaxOdts * AcquisitionPlanner.OdtDataFieldBytes);

        // 轮转接线：stop → 15×(SET_DAQ_PTR+WRITE_DAQ) → start，终态 Running。
        Assert.Equal(RotationTableState.Running, flow.Session.RotationTableState);
        var rotationRequests = flow.Spy.Sent.Select(f => f.Data.Span[0]).ToArray();
        Assert.Equal(XcpPid.StartStopDaqList, rotationRequests[0]);
        Assert.Equal(2, rotationRequests.Count(p => p == XcpPid.StartStopDaqList)); // stop + start
        Assert.Equal(30, rotationRequests.Count(p => p is XcpPid.SetDaqPtr or XcpPid.WriteDaq)); // 15 ODT × 2 帧

        // 计划空窗通知挂点（T11 IXcpPlanGapNotifier 经组合根接线）：stop 后恰一次。
        Assert.Equal(15, Assert.Single(flow.Gaps).OdtCount);

        // DAQ 侧 N 拍：按方案注入 3 拍 DTO，节奏 = 每拍 15 帧、60B（≤105B 上界）、PID 全部命中方案。
        Assert.Equal(3, flow.DaqBeats.Count);
        Assert.All(flow.DaqBeats, b =>
        {
            Assert.Equal(15, b.FrameCount);
            Assert.Equal(60, b.PayloadBytes);
            Assert.All(b.Pids, pid => Assert.Contains(pid, map.Odts.Select(o => o.Pid)));
        });

        // 轮询兜底在真机 965 方案上可跑（2 拍）：Executed 且成功值与归因失败互补并集为全量。
        Assert.All(flow.Polling, r => Assert.Equal(PollingCycleOutcome.Executed, r.Outcome));
        Assert.All(flow.Polling, r => Assert.Equal(map.PollingEntries.Count, r.Values.Count + r.Failures.Count));
        Assert.All(flow.Polling.SelectMany(r => r.Values), v =>
            Assert.Equal(map.PollingEntries.First(e => e.ObjectName == v.ObjectName).ByteLength, v.Data.Length));

        // (e) DOWNLOAD 禁用：全部发送帧无 0xF0 命令码。
        AssertNoDownload(flow.Spy);
    }
    // ---- (e) DOWNLOAD 禁用（专项）----

    [Fact]
    public async Task FullFlow_Never_Sends_Download_Command()
    {
        var flow = await RunRealSampleFullFlowAsync();

        // (e) 专项：规划+轮转+3 拍 DTO+2 拍轮询的全流程发送帧中，无 DOWNLOAD（0xF0）。
        Assert.NotEmpty(flow.Spy.Sent);
        AssertNoDownload(flow.Spy);
    }
    // ---- (b) 溢出降级路径端到端：轮询兜底量真实发生在模拟从机上 ----

    [Fact]
    public async Task Overflow_Polling_Fallback_Is_Acquired_From_Virtual_Slave()
    {
        // 缩小夹具：2×1B 测量进 DAQ + 1×8B VAL_BLK 溢出降级轮询（ObjectTooLarge）。
        var doc = SmallDoc(m =>
        {
            m.Measurements.Add(Meas("M0", A2lDataType.UBYTE, 0x1000));
            m.Measurements.Add(Meas("M1", A2lDataType.UBYTE, 0x1008));
            m.Characteristics = new[] { ValBlk("BigBlk", 0x1100, elements: 8) };
            m.Layouts = new[] { UbyteLayout() };
        });

        var slave = new XcpVirtualSlave();
        // 8B 量 > 7B → SET_MTA + UPLOAD 7B+1B 分块；从机按脚本回同一份 7B 载荷。
        slave.OverrideResponse(XcpPid.Upload,
            XcpPid.PositiveResponse, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77);
        using var session = NewSession(slave, out var spy);
        var map = session.Plan(Contracts(doc), Placeholders(doc));

        // planner 自产降级归因：8B 对象进轮询集合，DAQ 只装 2×1B。
        Assert.DoesNotContain(map.Odts.SelectMany(o => o.Entries), e => e.ObjectName == "BigBlk");
        var pollEntry = Assert.Single(map.PollingEntries);
        Assert.Equal("BigBlk", pollEntry.ObjectName);
        Assert.Equal(PlannedPollingCause.ObjectTooLarge, pollEntry.Cause);

        var rotation = await session.ConfigureRotationAsync(map);
        Assert.Equal(RotationTableState.Running, rotation.TableState);

        var result = Assert.Single(await session.RunPollingAsync(1));
        Assert.Equal(PollingCycleOutcome.Executed, result.Outcome);
        Assert.Empty(result.Failures);

        // 兜底量真实被采到：数据 = 模拟从机脚本回放的字节（7B chunk + MTA 自增后第 2 chunk 首字节）。
        var value = Assert.Single(result.Values);
        Assert.Equal("BigBlk", value.ObjectName);
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x11 }, value.Data);

        // 帧序列真实发生在模拟从机链路上：轮转 4 帧（stop/setptr/write/start）+ SET_MTA + 2×UPLOAD
        //（轮询数据与从机脚本一致即往返为真）。
        Assert.Equal(
            new byte[] { XcpPid.StartStopDaqList, XcpPid.SetDaqPtr, XcpPid.WriteDaq, XcpPid.SetDaqPtr, XcpPid.WriteDaq, XcpPid.StartStopDaqList, XcpPid.SetMta, XcpPid.Upload, XcpPid.Upload },
            spy.Sent.Select(f => f.Data.Span[0]).ToArray());

        AssertNoDownload(spy);
    }

    // ---- (c) 轮转失败恢复端到端 ----

    [Fact]
    public async Task Rewrite_Negative_Response_Is_Attributed_And_Recovered()
    {
        // stop 成功 → 首条 WRITE_DAQ 负响应（ERR_DAQ_ACTIVE）→ RetryRewrite 恢复 → 表 Running。
        var doc = SmallDoc(m => m.Measurements.Add(Meas("M0", A2lDataType.UBYTE, 0x1000)));
        var gaps = new List<RotationGapWindow>();
        var slave = new XcpVirtualSlave();
        var failFirst = new FailFirstWriteDaqTransport(slave, failCount: 1);
        using var session = NewSession(failFirst, out var spy,
            new XcpAcquisitionSessionOptions { GapNotifier = new RecordingGapNotifier(gaps) });
        var map = session.Plan(Contracts(doc), Placeholders(doc));

        var rotation = await session.ConfigureRotationAsync(
            map, new FixedRotationRecoveryPolicy(RotationRecoveryAction.RetryRewrite));

        // 归因：恢复成功的失败随 RotationResult 出站（Rewriting + ERR_DAQ_ACTIVE）。
        var failure = Assert.Single(rotation.RecoveredFailures);
        Assert.Equal(RotationPhase.Rewriting, failure.Phase);
        Assert.Equal(XcpError.DaqActive, failure.ErrorCode);
        Assert.Equal((ushort)0, failure.OdtIndex);
        Assert.Equal((ushort)0, failure.EntryIndex);
        Assert.Equal(RotationTableState.Running, rotation.TableState);

        // 恢复动作真实发生：帧序列 = stop → setptr → write(负) → setptr → write(重试成功) → start。
        Assert.Equal(
            new byte[] { XcpPid.StartStopDaqList, XcpPid.SetDaqPtr, XcpPid.WriteDaq, XcpPid.SetDaqPtr, XcpPid.WriteDaq, XcpPid.StartStopDaqList },
            spy.Sent.Select(f => f.Data.Span[0]).ToArray());
        Assert.Equal(0x00, spy.Sent[0].Data.Span[1]); // stop（mode 0）
        Assert.Equal(0x01, spy.Sent[^1].Data.Span[1]); // start（mode 1）

        // 空窗通知仍恰一次（stop 后、首条重写前）。
        Assert.Single(gaps);

        AssertNoDownload(spy);
    }

    [Fact]
    public async Task Rewrite_Negative_Response_Exhausted_Attributes_Terminal_Stop()
    {
        // 恢复耗尽：从机对 WRITE_DAQ 恒负响应 → 终态失败，表恒处 stop 态、绝不 start。
        var doc = SmallDoc(m => m.Measurements.Add(Meas("M0", A2lDataType.UBYTE, 0x1000)));
        var slave = new XcpVirtualSlave();
        slave.OverrideNegative(XcpPid.WriteDaq, XcpError.DaqActive);
        using var session = NewSession(slave, out var spy,
            new XcpAcquisitionSessionOptions { Rotation = new RotationSchedulerOptions { MaxRecoveryDecisions = 2 } });
        var map = session.Plan(Contracts(doc), Placeholders(doc));

        var ex = await Assert.ThrowsAsync<RotationFailedException>(() =>
            session.ConfigureRotationAsync(map, new FixedRotationRecoveryPolicy(RotationRecoveryAction.RetryRewrite)));

        // 归因值：Rewriting + ERR_DAQ_ACTIVE，表态 Stopped。
        Assert.Equal(RotationPhase.Rewriting, ex.Failure.Phase);
        Assert.Equal(XcpError.DaqActive, ex.Failure.ErrorCode);
        Assert.Equal(RotationTableState.Stopped, ex.TableState);

        // 帧序列：stop + 初始重写 + 2 次重试重写，无 start。
        Assert.Equal(
            new byte[] { XcpPid.StartStopDaqList, XcpPid.SetDaqPtr, XcpPid.WriteDaq, XcpPid.SetDaqPtr, XcpPid.WriteDaq, XcpPid.SetDaqPtr, XcpPid.WriteDaq },
            spy.Sent.Select(f => f.Data.Span[0]).ToArray());

        AssertNoDownload(spy);
    }

    // ---- (d) 采集覆盖：planner 自产方案 + 包侧 MissingCause（不经 IMapAlignmentService）----

    [Fact]
    public void RealSample_Coverage_Is_Computed_From_Planner_Plan_And_Package_MissingCause()
    {
        var doc = RealDoc();
        var contracts = Contracts(doc);
        using var session = NewSession(new XcpVirtualSlave(), out _);
        var map = session.Plan(contracts, Placeholders(doc));

        var coverage = session.ComputeCoverage(contracts, map);

        // 全量对账：每个合同对象恰一条覆盖条目。
        Assert.Equal(contracts.All.Count, coverage.Count);

        // planner 未承接的真机对象（5 个零长段对象）归因包侧 MissingCause=SegmentMissing，
        // 绝不静默失踪；其余全部被方案承接（DAQ 或轮询）。
        var missing = coverage.Where(c => !c.Covered).ToList();
        Assert.Equal(5, missing.Count);
        Assert.All(missing, c => Assert.Equal(MissingCause.SegmentMissing, c.MissingCause));
        Assert.All(missing, c => Assert.Null(c.PollingCause));

        var covered = coverage.Where(c => c.Covered).ToList();
        var daqObjects = map.Odts.SelectMany(o => o.Entries).Select(e => e.ObjectName).ToHashSet();
        var polledObjects = map.PollingEntries.Select(e => e.ObjectName).ToHashSet();
        Assert.Equal(daqObjects.Count + polledObjects.Count, covered.Count);
        Assert.All(covered.Where(c => daqObjects.Contains(c.ObjectName)), c => Assert.Null(c.PollingCause));
        Assert.All(covered.Where(c => !daqObjects.Contains(c.ObjectName)), c =>
            Assert.Equal(map.PollingEntries.First(e => e.ObjectName == c.ObjectName).Cause, c.PollingCause));
    }
    [Fact]
    public void Unlocatable_Object_Is_Missing_With_Package_MissingCause()
    {
        // ECU_ADDRESS 缺失 → planner 跳过（segments=0）→ 覆盖条目 MissingCause=SegmentMissing
        //（包侧 ValueNote 首因），绝不给"已覆盖"的假象。
        var doc = SmallDoc(m =>
        {
            m.Measurements.Add(Meas("M_OK", A2lDataType.UBYTE, 0x1000));
            m.Measurements.Add(Meas("M_NO_ADDR", A2lDataType.UBYTE, 0));
        });
        var contracts = Contracts(doc);
        var placeholders = Placeholders(doc);
        using var session = NewSession(new XcpVirtualSlave(), out _);
        var map = session.Plan(contracts, placeholders);

        var coverage = session.ComputeCoverage(contracts, map);
        Assert.Equal(2, coverage.Count);

        var ok = Assert.Single(coverage, c => c.ObjectName == "M_OK");
        Assert.True(ok.Covered);
        Assert.Null(ok.MissingCause);

        var missing = Assert.Single(coverage, c => c.ObjectName == "M_NO_ADDR");
        Assert.False(missing.Covered);
        Assert.Equal(MissingCause.SegmentMissing, missing.MissingCause);
    }

    // ---- 组合根 gate 接线顺序（T12 契约：置位 gate → WaitForQuietAsync → 轮转 → finally 复位）----

    [Fact]
    public async Task Polling_Beat_Yields_While_Rotation_In_Progress_And_Resumes_After()
    {
        var doc = SmallDoc(m =>
        {
            m.Measurements.Add(Meas("M0", A2lDataType.UBYTE, 0x1000));
            m.Characteristics = new[] { ValBlk("BigBlk", 0x1100, elements: 8) };
            m.Layouts = new[] { UbyteLayout() };
        });
        var slave = new XcpVirtualSlave();
        slave.DelayResponse(XcpPid.WriteDaq, TimeSpan.FromMilliseconds(150));
        slave.OverrideResponse(XcpPid.Upload,
            XcpPid.PositiveResponse, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77);
        using var session = NewSession(slave, out var spy);
        var map = session.Plan(Contracts(doc), Placeholders(doc));

        var rotation = session.ConfigureRotationAsync(map);
        await WaitUntilAsync(() => spy.Sent.Any(f => f.Data.Span[0] == XcpPid.WriteDaq));

        // 轮转进行中：轮询整拍让位（gate 置位后让位检查拦住，零线上流量）。
        var duringRotation = await session.PollBeatAsync();
        Assert.Equal(PollingCycleOutcome.SkippedRotation, duringRotation.Outcome);

        await rotation;
        Assert.Equal(RotationTableState.Running, session.RotationTableState);

        // finally 复位后：轮询恢复正常执行（真实 SET_MTA+UPLOAD 往返，8B 溢出对象兜底）。
        var after = await session.PollBeatAsync();
        Assert.Equal(PollingCycleOutcome.Executed, after.Outcome);
        var value = Assert.Single(after.Values);
        Assert.Equal("BigBlk", value.ObjectName);
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x11 }, value.Data);
    }

    // ------------------------------------------------------------------
    // 夹具与断言辅助
    // ------------------------------------------------------------------

    private sealed record FullFlow(XcpAcquisitionSession Session, XcpTransportSpy Spy, PlannedAcquisitionMap Map,
        ContractSet Contracts, AcquisitionPlan Placeholders, IReadOnlyList<RotationGapWindow> Gaps,
        IReadOnlyList<DaqBeat> DaqBeats, IReadOnlyList<PollingCycleResult> Polling);

    private sealed record DaqBeat(int FrameCount, int PayloadBytes, byte[] Pids);

    /// <summary>(a)/(e) 共用的真机 965 全流程：规划（×2 确定性）→ 轮转 → 3 拍 DTO → 2 拍轮询。</summary>
    private static async Task<FullFlow> RunRealSampleFullFlowAsync()
    {
        var doc = FullFlowDoc();
        var contracts = Contracts(doc);
        var placeholders = Placeholders(doc);
        var slave = new XcpVirtualSlave();

        var gaps = new List<RotationGapWindow>();
        var session = NewSession(
            slave, out var spy,
            new XcpAcquisitionSessionOptions { GapNotifier = new RecordingGapNotifier(gaps) });

        var map = session.Plan(contracts, placeholders);
        var map2 = session.Plan(contracts, placeholders);
        Assert.Equal(JsonSerializer.Serialize(map), JsonSerializer.Serialize(map2));

        var rotation = await session.ConfigureRotationAsync(map);
        Assert.Equal(RotationTableState.Running, rotation.TableState);
        Assert.Empty(rotation.RecoveredFailures);

        // DTO 捕获：spy.FrameReceived 转发 slave 出帧，按首字节过滤 DTO（0x00–0xFB）；
        // 每拍按方案逐 ODT 注入（payload 长度 = ODT 条目字节数和）。
        var beatFrames = new List<(byte Pid, int PayloadBytes)>();
        spy.FrameReceived += frame =>
        {
            var pid = frame.Data.Span[0];
            if (pid <= XcpPid.DaqDtoLast)
                beatFrames.Add((pid, frame.Data.Length - 1));
        };

        var beats = new List<DaqBeat>();
        for (var beat = 0; beat < 3; beat++)
        {
            beatFrames.Clear();
            foreach (var odt in map.Odts)
                slave.InjectDto((byte)odt.Pid, new byte[odt.Entries.Sum(e => e.ByteLength)]);
            var frames = beatFrames.ToArray();
            beats.Add(new DaqBeat(frames.Length, frames.Sum(f => f.PayloadBytes), frames.Select(f => f.Pid).ToArray()));
        }

        var polling = await session.RunPollingAsync(2);
        return new FullFlow(session, spy, map, contracts, placeholders, gaps, beats, polling);
    }
    private static void AssertNoDownload(XcpTransportSpy spy)
    {
        var download = spy.Sent.Count(f => f.Data.Span[0] == XcpPid.Download);
        Assert.True(download == 0,
            $"DOWNLOAD (0x{XcpPid.Download:X2}) must never be sent by the scheduling layer (spec §0/D2); " +
            $"spy recorded {download} such frame(s).");
    }

    private static XcpMasterOptions MasterOptions() => new(XcpVirtualSlave.DefaultMasterCanId);

    private static XcpAcquisitionSession NewSession(
        IXcpTransport transport, out XcpTransportSpy spy, XcpAcquisitionSessionOptions? options = null)
    {
        // 测试统一用 1ms 轮询周期（RunPollingAsync 首拍前等一个周期；默认 1s 会拖慢用例）。
        spy = new XcpTransportSpy(transport);
        return new XcpAcquisitionSession(spy, MasterOptions(), WithFastPolling(options));
    }

    private static XcpAcquisitionSessionOptions WithFastPolling(XcpAcquisitionSessionOptions? options) => new()
    {
        Polling = options?.Polling ?? new PollingSchedulerOptions { Period = TimeSpan.FromMilliseconds(1) },
        Rotation = options?.Rotation,
        GapNotifier = options?.GapNotifier,
    };

    /// <summary>真机 A2L + 测试侧补 RAM 映射段（见类头夹具说明）。</summary>
    private static A2lDocument FullFlowDoc()
    {
        var doc = RealDoc();
        var module = doc.Modules[0];
        var ramProbe = new A2lMemorySegment(
            "RAM_PROBE", "T13 test fixture: mapping the sample A2L omits for its measurement RAM",
            "DATA", "RAM_INTERN", "INTERN", 0x1FFE0000, 0x30000, Array.Empty<string>(), new LineRange(1, 1),
            IfDataXcp: new XcpIfData(
                XcpIfDataScope.MemorySegmentLevel, null, null, null, null,
                Array.Empty<XcpOnCan>(),
                new[]
                {
                    new XcpSegment(
                        0, 1, 0, 0, 0,
                        new[] { new XcpAddressMapping(0x1FFE0000, 0x1FFE0000, 0x30000, Array.Empty<XcpMissingField>(), "") },
                        1, Array.Empty<XcpMissingField>(), ""),
                },
                Array.Empty<A2lUnknownBlock>(), Array.Empty<XcpMissingField>(), ""));
        return doc with
        {
            Modules = new[] { module with { MemorySegments = (module.MemorySegments ?? Array.Empty<A2lMemorySegment>()).Append(ramProbe).ToArray() } },
        };
    }

    private static A2lDocument RealDoc()
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "TestData", "App_merge_INCA.a2l");
        var parsed = Asap2PackageApi.ParseFile(path);
        Assert.NotNull(parsed.Value);
        return parsed.Value!;
    }

    private static ContractSet Contracts(A2lDocument doc) => Asap2PackageApi.Contracts(doc);

    private static AcquisitionPlan Placeholders(A2lDocument doc) => AcquisitionPlan.Build(Contracts(doc));

    private sealed class RecordingGapNotifier : IXcpPlanGapNotifier
    {
        private readonly List<RotationGapWindow> _gaps;
        public RecordingGapNotifier(List<RotationGapWindow> gaps) => _gaps = gaps;
        public void OnPlanGapWindow(RotationGapWindow window) => _gaps.Add(window);
    }

    /// <summary>
    /// 测试本地一次性失败注入：前 failCount 条 WRITE_DAQ 不转发到从机，
    /// 直接以 ERR_DAQ_ACTIVE 负应答（stop-后-重写失败场景的精确注入点）。
    /// </summary>
    private sealed class FailFirstWriteDaqTransport : IXcpTransport
    {
        private readonly IXcpTransport _inner;
        private readonly CanId _slaveCanId;
        private int _remaining;

        public FailFirstWriteDaqTransport(IXcpTransport inner, int failCount)
        {
            _inner = inner;
            _slaveCanId = XcpVirtualSlave.DefaultSlaveCanId;
            _remaining = failCount;
            inner.FrameReceived += Forward;
        }

        public event Action<CanFrame>? FrameReceived;

        public long FramesDropped => _inner.FramesDropped;

        public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
        {
            if (frame.Data.Span[0] == XcpPid.WriteDaq && Interlocked.Decrement(ref _remaining) >= 0)
            {
                FrameReceived?.Invoke(new CanFrame(
                    _slaveCanId,
                    new ReadOnlyMemory<byte>(new[] { XcpPid.Error, (byte)XcpError.DaqActive }),
                    FrameFlags.None, ChannelId.None, default));
                return ValueTask.FromResult(Result<Unit>.Ok(default));
            }
            return _inner.WriteAsync(frame, ct);
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();

        private void Forward(CanFrame frame) => FrameReceived?.Invoke(frame);
    }

    // ---- 缩小夹具构建器（T9 测试同款口径：单模块 + 单 DAQ 表 + 单段映射）----

    private sealed class ModuleSpec
    {
        public List<A2lMeasurement> Measurements = [];
        public A2lCharacteristic[] Characteristics = [];
        public A2lRecordLayout[] Layouts = [];
        public XcpDaqList? DaqList = DaqList(firstPid: 0, maxOdtEntries: 100);
        public A2lMemorySegment[] Segments = [CalSegment()];
    }

    private static A2lDocument SmallDoc(Action<ModuleSpec> configure)
    {
        var spec = new ModuleSpec();
        configure(spec);

        var module = new A2lModule("M", "m",
            spec.Measurements, spec.Characteristics, Array.Empty<A2lAxisPts>(),
            new[] { CmIdentical }, spec.Layouts, Array.Empty<A2lGroup>(), null,
            Array.Empty<A2lAxisDescr>(), Array.Empty<A2lUserRights>(),
            Array.Empty<A2lVersionInfo>(), Array.Empty<A2lAxisPtsX>(),
            new LineRange(1, 1), MemorySegments: spec.Segments,
            IfDataXcp: XcpIfData(spec.DaqList));
        return new A2lDocument(A2lVersion.V1_6x, "P", "", "",
            new A2lModCommon("", A2lByteOrder.MSB_LAST, null, null, null, new LineRange(1, 1)),
            new[] { module }, "", 1);
    }

    private static XcpIfData? XcpIfData(XcpDaqList? daqList)
    {
        if (daqList is null)
            return null;
        var daq = new XcpDaq(
            Dynamic: false, MaxDaq: 1, MaxEventChannel: 1, MinDaq: null,
            OptimisationType: "STATIC", AddressExtension: "", IdentificationFieldType: "",
            GranularityOdtEntrySizeDaq: "", MaxOdtEntrySizeDaq: 4, OverloadIndication: false,
            new[] { daqList }, Array.Empty<XcpEventChannel>(),
            Array.Empty<XcpMissingField>(), "");
        return new XcpIfData(XcpIfDataScope.ModuleLevel, null, daq, null, null,
            Array.Empty<XcpOnCan>(), Array.Empty<XcpSegment>(),
            Array.Empty<A2lUnknownBlock>(), Array.Empty<XcpMissingField>(), "");
    }

    private static XcpDaqList DaqList(ushort firstPid, uint maxOdtEntries, uint number = 0) =>
        new(number, "DAQ", 15, maxOdtEntries, firstPid, 0, "");

    private static A2lCompuMethod CmIdentical =>
        new("CM_ID", "id", "IDENTICAL", "%d", "-", new IdenticalConversion(), new LineRange(1, 1));

    private static A2lMeasurement Meas(string name, A2lDataType dt, ulong addr) =>
        new(name, "d", dt, "CM_ID", "0", "0", "0", "65535", addr, new LineRange(1, 1));

    private static A2lCharacteristic ValBlk(string name, ulong addr, int elements) =>
        new(name, "d", "VAL_BLK", "RL_U8", addr, "0", "100", null, "CM_ID", new LineRange(1, 1),
            MatrixDim: new uint[] { (uint)elements, 1, 1 });

    private static A2lRecordLayout UbyteLayout() =>
        new("RL_U8",
            new[] { new RecordLayoutEntry("FNC_VALUES", 0, "UBYTE", "COLUMN_DIR", "DIRECT", null, null) },
            new LineRange(1, 1));

    private static A2lMemorySegment CalSegment() =>
        new("CAL", "cal", "DATA", "FLASH", "INTERN", 0x1000, 0x1000,
            Array.Empty<string>(), new LineRange(1, 1),
            IfDataXcp: new XcpIfData(XcpIfDataScope.MemorySegmentLevel, null, null, null, null,
                Array.Empty<XcpOnCan>(),
                new[]
                {
                    new XcpSegment(0, 1, 0, 0, 0,
                        new[] { new XcpAddressMapping(0x1000, 0x9000, 0x1000, Array.Empty<XcpMissingField>(), "") },
                        1, Array.Empty<XcpMissingField>(), ""),
                },
                Array.Empty<A2lUnknownBlock>(), Array.Empty<XcpMissingField>(), ""));

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        for (var elapsed = 0; elapsed < timeoutMs && !condition(); elapsed += 10)
            await Task.Delay(10);
        Assert.True(condition(), "condition not met within timeout");
    }
}