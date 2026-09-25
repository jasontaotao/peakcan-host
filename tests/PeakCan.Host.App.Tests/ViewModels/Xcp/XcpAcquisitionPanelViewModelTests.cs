using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using A2lEditor.Core;
using A2lEditor.Core.IfData;
using A2lEditor.Core.Layout;
using A2lEditor.Core.Model;
using A2lEditor.Core.Parsing;
using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.App.ViewModels.Xcp;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Xcp.Abstractions;
using PeakCan.Host.Core.Xcp.Capability;
using PeakCan.Host.Core.Xcp.Protocol;
using PeakCan.Host.Core.Xcp.Scheduling;
using Xunit;
// 本文件构造 A2L 最小模型，显式指回 System.IO.File/Path（repo 内同名类型冲突先例）。
using File = System.IO.File;
using Path = System.IO.Path;

namespace PeakCan.Host.App.Tests.ViewModels.Xcp;

/// <summary>
/// S3-T7 红测：XcpAcquisitionPanelViewModel（spec D6 生命周期）。
/// <para>
/// (a) Start 顺序 = 对账（CONNECT+能力实测+Reconcile）→ Plan → ConfigureRotation →
/// 轮询，且 MarkConnected 被调；(b) 对账拒绝 → Start 失败 + XcpCapabilityReport
/// 拒绝明细进状态区，无静默启动；(c) ComputeCoverage 三列直读展示；
/// (d) Stop 幂等 + Dispose 后回 Idle 允许再 Start（T3 Loaded 语义衔接）；
/// (e) 运行中禁止重 Start（IsAcquiring 门禁）。另有门禁/失败路径/审计证据补充。
/// </para>
/// <para>
/// 传输替身：App 测试内自建轻量脚本化 mock transport（按命令 PID 回放
/// XcpGoldenSamples 黄金样本正响应，可篡改），模拟从机形状借用 Core.Tests
/// XcpVirtualSlave（不跨测试工程引用）。
/// </para>
/// </summary>
public class XcpAcquisitionPanelViewModelTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        foreach (var path in _tempFiles)
        {
            try { File.Delete(path); } catch (IOException) { /* best effort */ }
        }
    }

    private string TempA2lFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"xcp-t7-vm-{Guid.NewGuid():N}.a2l");
        File.WriteAllText(path, "ASAP2_VERSION 1 40\n");
        _tempFiles.Add(path);
        return path;
    }

    /// <summary>声明 OPTIONAL_CMD（与 VM 实测链一一对应；CA1861：常量数组提为字段）。</summary>
    private static readonly int[] NoteLines = { 8 };

    private static readonly string[] DeclaredOptionalCommands =
    [
        "GET_COMM_MODE_INFO", "SET_MTA", "UPLOAD", "SHORT_UPLOAD",
        "SET_DAQ_PTR", "WRITE_DAQ", "CLEAR_DAQ_LIST",
        "START_STOP_DAQ_LIST", "START_STOP_SYNCH",
        "GET_DAQ_PROCESSOR_INFO", "GET_DAQ_RESOLUTION_INFO",
        "GET_DAQ_LIST_INFO", "GET_DAQ_EVENT_INFO",
    ];

    /// <summary>
    /// 单模块 + 单段映射 + 三个合同对象的最小 A2L 文档：
    /// EngineSpeed（1B，已映射 → DAQ 打包）、BigTable（8B VAL_BLK，已映射 →
    /// 轮询降级 ObjectTooLarge）、LostSignal（ECU_ADDRESS 缺失（0x0）→ planner
    /// 未承接，覆盖清单 MissingCause 有值）。声明侧与 XcpGoldenSamples 从机一致。
    /// modules=2 变体供 planner 单模块 fail-loud 用例使用。
    /// </summary>
    private static A2lDocument SmallDoc(int modules = 1)
    {
        var cmIdentical = new A2lCompuMethod("CM_ID", "id", "IDENTICAL", "%d", "-",
            new IdenticalConversion(), new LineRange(1, 1));
        var layout = new A2lRecordLayout("RL_U8",
            new[] { new RecordLayoutEntry("FNC_VALUES", 0, "UBYTE", "COLUMN_DIR", "DIRECT", null, null) },
            new LineRange(1, 1));

        A2lMeasurement Meas(string name, ulong addr) =>
            new(name, "d", A2lDataType.UBYTE, "CM_ID", "0", "0", "0", "65535", addr, new LineRange(1, 1));

        var bigTable = new A2lCharacteristic("BigTable", "d", "VAL_BLK", "RL_U8", 0x1010,
            "0", "100", null, "CM_ID", new LineRange(1, 1), MatrixDim: new uint[] { 8, 1, 1 });

        var memorySegment = new A2lMemorySegment("CAL", "cal", "DATA", "FLASH", "INTERN",
            0x1000, 0x1000, Array.Empty<string>(), new LineRange(1, 1),
            IfDataXcp: SegmentIfData());

        var module = new A2lModule("M", "m",
            new A2lMeasurement[] { Meas("EngineSpeed", 0x1000), Meas("LostSignal", 0) },
            new[] { bigTable }, Array.Empty<A2lAxisPts>(),
            new[] { cmIdentical }, new[] { layout }, Array.Empty<A2lGroup>(), null,
            Array.Empty<A2lAxisDescr>(), Array.Empty<A2lUserRights>(),
            Array.Empty<A2lVersionInfo>(), Array.Empty<A2lAxisPtsX>(),
            new LineRange(1, 1), MemorySegments: new[] { memorySegment },
            IfDataXcp: ModuleIfData());
        return new A2lDocument(A2lVersion.V1_6x, "P", "", "",
            new A2lModCommon("", A2lByteOrder.MSB_LAST, null, null, null, new LineRange(1, 1)),
            modules == 1 ? new[] { module } : new[] { module, module }, "", 1);
    }

    /// <summary>模块级 XCP IF_DATA：声明侧与黄金样本从机逐字段一致（对账零拒绝）。</summary>
    private static XcpIfData ModuleIfData(bool withOnCan = true, bool declaresDownload = false)
    {
        var protocolLayer = new XcpProtocolLayer(
            SourceVersion: "1.0", XcpVersion: 0x0100,
            T1Ms: 2000, T2Ms: 20, T3Ms: 20, T4Ms: 1000, T5Ms: 10, T6Ms: 10, T7Ms: 2000,
            MaxCto: 8, MaxDto: 8,
            ByteOrder: XcpByteOrder.MsbLast, AddressGranularity: XcpAddressGranularity.Byte,
            OptionalCommands: declaresDownload
                ? DeclaredOptionalCommands.Append("DOWNLOAD").ToArray()
                : DeclaredOptionalCommands,
            BlockModeSupportedBySlave: false, BlockModeSupportedByMaster: false,
            MaxBlockSize: null, MinStPin: null,
            ByteOrderLine: 1, Missing: Array.Empty<XcpMissingField>(), SourceText: "");

        var daq = new XcpDaq(
            Dynamic: false, MaxDaq: 1, MaxEventChannel: 1, MinDaq: null,
            OptimisationType: "STATIC", AddressExtension: "", IdentificationFieldType: "",
            GranularityOdtEntrySizeDaq: "", MaxOdtEntrySizeDaq: 4, OverloadIndication: false,
            new[] { new XcpDaqList(0, "DAQ", 15, 100, 0, 0, "") },
            new[] { new XcpEventChannel("EV", "ev", 0, "DAQ", 1, 10, 6, 0, 10000, "UNIT_1MS") },
            Array.Empty<XcpMissingField>(), "");

        var onCan = new XcpOnCan(
            CanVersion: 0x0100, MasterCanIdRaw: 0x18FFF667, SlaveCanIdRaw: 0x18FFF666,
            Baudrate: 500000, SamplePoint: 0x4B, SampleRate: "SINGLE",
            BtlCycles: null, Sjw: null, SyncEdge: "SINGLE", MaxDlcRequired: false,
            MasterCanIdLine: 0, SlaveCanIdLine: 0,
            Missing: Array.Empty<XcpMissingField>(), SourceText: "");

        return new XcpIfData(XcpIfDataScope.ModuleLevel, protocolLayer, daq, null, null,
            withOnCan ? new[] { onCan } : Array.Empty<XcpOnCan>(), Array.Empty<XcpSegment>(),
            Array.Empty<A2lUnknownBlock>(), Array.Empty<XcpMissingField>(), "");
    }

    /// <summary>段级 XCP IF_DATA：逻辑 0x1000 起 0x1000 长的地址映射（覆盖 EngineSpeed/BigTable 的逻辑地址）。</summary>
    private static XcpIfData SegmentIfData() =>
        new(XcpIfDataScope.MemorySegmentLevel, null, null, null, null,
            Array.Empty<XcpOnCan>(),
            new[]
            {
                new XcpSegment(0, 1, 0, 0, 0,
                    new[] { new XcpAddressMapping(0x1000, 0x9000, 0x1000, Array.Empty<XcpMissingField>(), "") },
                    1, Array.Empty<XcpMissingField>(), ""),
            },
            Array.Empty<A2lUnknownBlock>(), Array.Empty<XcpMissingField>(), "");

    /// <summary>默认 Loaded 夹具（可变体：无 XCP_ON_CAN / 声明 DOWNLOAD / 附加 ValidationNote）。</summary>
    private static XcpA2lLoadResult.Loaded DefaultLoaded(
        bool withOnCan = true,
        bool declaresDownload = false,
        ValidationNote[]? notes = null) =>
        new(SmallDoc(), ModuleIfData(withOnCan, declaresDownload),
            notes ?? Array.Empty<ValidationNote>(), new ContractSet(SmallDoc()));

    private XcpConnectionPanelViewModel ConnectionPanel(out ConnectedChannelsSource source)
    {
        return ConnectionPanelWithLoaded(DefaultLoaded(), out source);
    }

    private XcpConnectionPanelViewModel ConnectionPanelWithLoaded(
        XcpA2lLoadResult.Loaded loaded, out ConnectedChannelsSource source)
    {
        source = new ConnectedChannelsSource();
        source.Publish(new[]
        {
            new HilViewModel.ConnectedChannel(0x51, BaudRate.Can500kbps, Fd: false, "USB1", Channel: new FakeChannel()),
        });
        var connection = new XcpConnectionPanelViewModel(loadA2l: _ => loaded, connectedChannels: source);
        connection.A2lPath = TempA2lFile();
        connection.LoadA2LCommand.Execute(null);
        connection.SelectedChannel = source.Current[0];
        return connection;
    }

    /// <summary>组装好的采集面板 VM（黄金样本从机 + 可篡改 transport 注入）。</summary>
    private static XcpAcquisitionPanelViewModel NewVm(
        XcpConnectionPanelViewModel connection,
        ScriptedTransport transport,
        XcpAcquisitionSessionOptions? sessionOptions = null)
    {
        return new XcpAcquisitionPanelViewModel(
            connection: connection,
            transportFactory: _ => transport,
            sessionOptions: sessionOptions);
    }

    private static XcpAcquisitionSessionOptions FastPollingOptions() =>
        new() { Polling = new PollingSchedulerOptions { Period = TimeSpan.FromMilliseconds(5) } };

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        for (var elapsed = 0; elapsed < timeoutMs && !condition(); elapsed += 10)
            await Task.Delay(10);
        Assert.True(condition(), "condition not met within timeout");
    }

    // ------------------------------------------------------------------
    // 传输替身：按命令 PID 回放黄金样本，可篡改；记录发送帧序列
    // ------------------------------------------------------------------

    private sealed class ScriptedTransport : IXcpTransport
    {
        private static readonly Dictionary<byte, byte[]> GoldenResponses = new()
        {
            [XcpPid.Connect] = XcpGoldenSamples.ConnectPositiveResponse.ToArray(),
            [XcpPid.GetCommModeInfo] = XcpGoldenSamples.GetCommModeInfoPositiveResponse.ToArray(),
            [XcpPid.GetDaqProcessorInfo] = XcpGoldenSamples.GetDaqProcessorInfoPositiveResponse.ToArray(),
            [XcpPid.GetDaqResolutionInfo] = XcpGoldenSamples.GetDaqResolutionInfoPositiveResponse.ToArray(),
            [XcpPid.GetDaqListInfo] = XcpGoldenSamples.GetDaqListInfoPositiveResponse.ToArray(),
            [XcpPid.GetDaqEventInfo] = XcpGoldenSamples.GetDaqEventInfoPositiveResponse.ToArray(),
        };

        private static readonly CanId SlaveCanId = new(0x18FFF666, FrameFormat.Extended);

        private readonly object _gate = new();
        private readonly Dictionary<byte, byte[]> _overrides = new();
        private readonly List<byte> _sentPids = new();
        private readonly List<CanFrame> _sentFrames = new();
        private readonly HashSet<byte> _silentPids = new();

        public event Action<CanFrame>? FrameReceived;
        public long FramesDropped => 0;
        public bool Disposed { get; private set; }

        /// <summary>写帧抛异常（模拟底层 channel 写失败路径）。</summary>
        public bool ThrowOnWrite { get; set; }

        public IReadOnlyList<byte> SentPids
        {
            get { lock (_gate) return _sentPids.ToArray(); }
        }

        public IReadOnlyList<CanFrame> SentFrames
        {
            get { lock (_gate) return _sentFrames.ToArray(); }
        }

        /// <summary>静默（超时模拟）：命令不发响应帧。</summary>
        public void Silence(byte pid)
        {
            lock (_gate) _silentPids.Add(pid);
        }

        public void OverrideResponse(byte pid, params byte[] response)
        {
            lock (_gate) _overrides[pid] = response;
        }

        public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
        {
            if (ThrowOnWrite)
                throw new InvalidOperationException("simulated channel write failure");

            byte[]? response;
            lock (_gate)
            {
                var pid = frame.Data.Span[0];
                _sentPids.Add(pid);
                _sentFrames.Add(frame);
                response = _silentPids.Contains(pid)
                    ? null
                    : _overrides.TryGetValue(pid, out var overridden)
                        ? overridden
                        : GoldenResponses.TryGetValue(pid, out var golden)
                            ? golden
                            : new byte[] { XcpPid.PositiveResponse };
            }

            if (response is null)
                return ValueTask.FromResult(Result<Unit>.Ok(default)); // 静默：不派发响应帧（超时语义）

            FrameReceived?.Invoke(new CanFrame(
                SlaveCanId,
                new ReadOnlyMemory<byte>(response),
                FrameFlags.None, ChannelId.None, default));
            return ValueTask.FromResult(Result<Unit>.Ok(default));
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeChannel : ICanChannel
    {
        public ChannelId Id => ChannelId.None;
        public bool IsConnected => true;
        // 替身不产生读线程帧：接口事件占位（CS0067 关闭，仅本测试替身）。
#pragma warning disable CS0067
        public event Action<CanFrame>? FrameReceived;
        public event Action<ReadLoopError>? ReadLoopError;
#pragma warning restore CS0067

        public Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default) =>
            Task.FromResult(Result<Unit>.Ok(default));

        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default) =>
            ValueTask.FromResult(Result<Unit>.Ok(default));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ------------------------------------------------------------------
    // (a) Start 顺序：对账 → Plan → ConfigureRotation → 轮询；MarkConnected 被调
    // ------------------------------------------------------------------

    [Fact]
    public async Task Start_runs_reconcile_then_plan_then_rotation_then_polling_and_marks_connected()
    {
        var connection = ConnectionPanel(out _);
        var transport = new ScriptedTransport();
        var vm = NewVm(connection, transport, FastPollingOptions());

        var started = await vm.StartAsync();

        // StartAsync 返回时刻：对账 + Plan + ConfigureRotation 已同步完成，已 MarkConnected。
        started.Should().BeTrue("黄金样本从机声明一致，对账放行");
        vm.IsAcquiring.Should().BeTrue();
        connection.ConnectionState.Should().Be(XcpConnectionState.Connected, "对账通过后 MarkConnected 被调");

        // 对账先行：第一帧必是 CONNECT，随后是能力查询（GET_COMM_MODE_INFO 等）。
        var pids = transport.SentPids;
        pids.Should().NotBeEmpty();
        pids[0].Should().Be(XcpPid.Connect, "Start 第一步是对账（CONNECT + 能力实测）");
        pids.Should().Contain(XcpPid.GetDaqProcessorInfo);
        pids.Should().Contain(XcpPid.GetDaqEventInfo);

        // 轮转在轮询前：START_STOP_DAQ_LIST 共 3 次 = 探测 1 次 + 轮转 stop/start 各 1 次；
        // 两次轮转帧之间只能是 SET_DAQ_PTR/WRITE_DAQ（单 ODT 方案）。
        var daqListIndexes = new List<int>();
        for (var i = 0; i < pids.Count; i++)
            if (pids[i] == XcpPid.StartStopDaqList)
                daqListIndexes.Add(i);
        daqListIndexes.Should().HaveCount(3, "能力探测 1 次 + 轮转 stop/start 各 1 次");
        var rotationStop = daqListIndexes[1];
        var rotationEnd = daqListIndexes[2];

        // 轮询首拍帧出现后再取快照（首拍前先等一个周期，FastPolling 5ms——
        // StartAsync 返回时刻轮询循环已在跑但首拍帧未必已上总线）。
        await WaitUntilAsync(() => transport.SentPids.Count > rotationEnd + 1);
        pids = transport.SentPids;
        pids.Skip(rotationStop + 1).Take(rotationEnd - rotationStop - 1)
            .Should().OnlyContain(p => p == XcpPid.SetDaqPtr || p == XcpPid.WriteDaq, "轮转中段 = DAQ 表重写");
        pids[rotationEnd + 1].Should().Be(XcpPid.SetMta, "轮转结束后进入轮询（BigTable 8B → SET_MTA+UPLOAD）");
        pids.Skip(rotationEnd + 1).Should().Contain(XcpPid.Upload, "8B 轮询条目经 UPLOAD 分块读取");

        // 轮询持续运行：等待第二拍（SET_MTA/UPLOAD ×2）出现。
        var framesAfterFirstPollBeat = rotationEnd + 1 + 3;
        await WaitUntilAsync(() => transport.SentPids.Count >= framesAfterFirstPollBeat);

        await vm.StopAsync();
    }

    // ------------------------------------------------------------------
    // (b) 对账拒绝：声明与实测不一致 → Start 失败、无静默启动、拒绝明细进状态区
    // ------------------------------------------------------------------

    [Fact]
    public async Task Start_rejected_on_reconcile_mismatch_without_silent_acquisition()
    {
        var connection = ConnectionPanel(out _);
        var transport = new ScriptedTransport();
        // 篡改 GET_DAQ_PROCESSOR_INFO：实测 MAX_DAQ=2 vs 声明 MAX_DAQ=1 → 拒绝。
        transport.OverrideResponse(XcpPid.GetDaqProcessorInfo,
            0xFF, 0x02, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00);
        var vm = NewVm(connection, transport);

        var started = await vm.StartAsync();

        started.Should().BeFalse("硬约束字段不一致必须拒绝启动（宁可不采不错采）");
        vm.IsAcquiring.Should().BeFalse();
        connection.ConnectionState.Should().Be(XcpConnectionState.Loaded, "对账拒绝不得 MarkConnected");
        vm.CapabilityReport.Should().NotBeNull();
        vm.CapabilityReport!.RejectedStart.Should().BeTrue();
        vm.CapabilityReport.Findings.Should().Contain(f =>
            f.Code == "MAX_DAQ_MISMATCH" && f.Severity == XcpCapabilitySeverity.Reject);

        // 拒绝明细进状态区（无静默）：人读行含 [Reject] + Code。
        vm.StatusLines.Should().Contain(l => l.Contains("[Reject]") && l.Contains("MAX_DAQ_MISMATCH"));

        // 无静默启动：发送帧止于能力探测序列（probe 段本身含 SET_MTA/UPLOAD/
        // START_STOP_DAQ_LIST 良性帧），其后不得再出现轮转/轮询帧。
        var pids = transport.SentPids;
        pids.Should().EndWith(XcpPid.StartStopSynch, "发送帧止于能力探测序列");
        pids.Count(p => p == XcpPid.StartStopDaqList).Should().Be(1, "只允许探测帧，不得配置轮转（轮转 = stop + start）");
        pids.Count(p => p == XcpPid.SetMta).Should().Be(1, "只允许探测帧，不得进入轮询");
        pids.Count(p => p == XcpPid.Upload).Should().Be(1, "只允许探测帧，不得进入轮询");
        vm.CoverageEntries.Should().BeEmpty("未 Plan 就拒绝，覆盖清单不得出现（避免陈旧数据说谎）");
        transport.Disposed.Should().BeTrue("拒绝路径必须释放会话与 transport");
    }

    // ------------------------------------------------------------------
    // (c) 覆盖清单：ComputeCoverage 三列（Covered/PollingCause/MissingCause）直读展示
    // ------------------------------------------------------------------

    [Fact]
    public async Task Coverage_entries_show_three_columns_directly_from_compute_coverage()
    {
        var connection = ConnectionPanel(out _);
        var transport = new ScriptedTransport();
        var vm = NewVm(connection, transport, FastPollingOptions());

        (await vm.StartAsync()).Should().BeTrue();

        var rows = vm.CoverageEntries;
        rows.Should().HaveCount(3, "ComputeCoverage 逐合同对象出三列行");

        var engine = rows.Should().ContainSingle(r => r.ObjectName == "EngineSpeed").Subject;
        engine.Covered.Should().BeTrue("1B 已映射对象由 DAQ 打包承接");
        engine.PollingCause.Should().BeNull();
        engine.MissingCause.Should().BeNull();

        var bigTable = rows.Should().ContainSingle(r => r.ObjectName == "BigTable").Subject;
        bigTable.Covered.Should().BeTrue("8B 对象由轮询降级承接，仍算覆盖");
        bigTable.PollingCause.Should().Be(PlannedPollingCause.ObjectTooLarge, "planner 自产降级归因直读");
        bigTable.MissingCause.Should().BeNull();

        var lost = rows.Should().ContainSingle(r => r.ObjectName == "LostSignal").Subject;
        lost.Covered.Should().BeFalse("ECU_ADDRESS 缺失 → planner 未承接");
        lost.PollingCause.Should().BeNull();
        lost.MissingCause.Should().NotBeNull("包侧 MissingCause 直读展示");

        await vm.StopAsync();
    }

    // ------------------------------------------------------------------
    // (d) Stop 幂等 + Dispose 后回 Idle 允许再 Start（T3 Loaded 语义衔接）
    // ------------------------------------------------------------------

    [Fact]
    public async Task Stop_is_idempotent_and_allows_restart_after_dispose()
    {
        var connection = ConnectionPanel(out _);
        var first = new ScriptedTransport();
        var second = new ScriptedTransport();
        // 工厂按调用次序返回：第一次 Start 用 first，重启用 second。
        var queue = new Queue<ScriptedTransport>(new[] { first, second });
        var vm = new XcpAcquisitionPanelViewModel(
            connection: connection,
            transportFactory: _ => queue.Dequeue(),
            sessionOptions: FastPollingOptions());

        // 未启动时 Stop：不炸、无状态漂移（幂等）。
        await vm.StopAsync();
        vm.IsAcquiring.Should().BeFalse();
        connection.ConnectionState.Should().Be(XcpConnectionState.Loaded);
        first.Disposed.Should().BeFalse("未启动过无会话可释放");

        // Start → Stop：Dispose 会话 + transport，状态回 Loaded（A2L 仍有效）。
        (await vm.StartAsync()).Should().BeTrue();
        await vm.StopAsync();
        vm.IsAcquiring.Should().BeFalse("Stop 后回 Idle");
        connection.ConnectionState.Should().Be(XcpConnectionState.Loaded,
            "T3 修复语义：A2L 已加载时 MarkDisconnected 回 Loaded，重连可直接再 Start");
        first.Disposed.Should().BeTrue("Stop 必须释放会话与 transport");

        // Dispose 后允许再 Start：全新会话 + transport，同样走完整生命周期。
        (await vm.StartAsync()).Should().BeTrue();
        vm.IsAcquiring.Should().BeTrue();
        second.Disposed.Should().BeFalse("新会话在跑，不得被误释放");
        await vm.StopAsync();
        second.Disposed.Should().BeTrue();
    }

    // ------------------------------------------------------------------
    // (e) 运行中禁止重 Start（IsAcquiring 门禁）
    // ------------------------------------------------------------------

    [Fact]
    public async Task Start_while_acquiring_is_rejected_by_isAcquiring_gate()
    {
        var connection = ConnectionPanel(out _);
        var transport = new ScriptedTransport();
        var vm = NewVm(connection, transport, FastPollingOptions());

        (await vm.StartAsync()).Should().BeTrue();
        var pidsBefore = transport.SentPids.Count;

        var second = await vm.StartAsync();

        second.Should().BeFalse("IsAcquiring 门禁：运行中禁止重 Start（S2 并发契约是调用侧义务）");
        vm.IsAcquiring.Should().BeTrue("第一次会话不受影响");
        vm.StatusLines.Should().Contain(l => l.Contains("禁止重 Start"));
        transport.SentPids.Count.Should().Be(pidsBefore, "重 Start 被拦，不得与在途会话并发打帧");

        await vm.StopAsync();
    }

    // ------------------------------------------------------------------
    // 门禁 / 失败路径 / 审计证据补充（D6 生命周期语义内）
    // ------------------------------------------------------------------

    [Fact]
    public async Task Start_fails_gracefully_without_connection_panel()
    {
        var vm = new XcpAcquisitionPanelViewModel();

        (await vm.StartAsync()).Should().BeFalse("无连接面板时不得启动");
        vm.StatusLines.Should().Contain(l => l.Contains("A2L 未加载"));
        vm.IsAcquiring.Should().BeFalse();
    }

    [Fact]
    public async Task Start_fails_when_a2l_not_loaded()
    {
        var source = new ConnectedChannelsSource();
        source.Publish(new[]
        {
            new HilViewModel.ConnectedChannel(0x51, BaudRate.Can500kbps, Fd: false, "USB1", Channel: new FakeChannel()),
        });
        var connection = new XcpConnectionPanelViewModel(connectedChannels: source);
        connection.SelectedChannel = source.Current[0];

        var factoryCalled = false;
        var vm = new XcpAcquisitionPanelViewModel(
            connection: connection,
            transportFactory: _ => { factoryCalled = true; return new ScriptedTransport(); });

        (await vm.StartAsync()).Should().BeFalse();
        vm.StatusLines.Should().Contain(l => l.Contains("A2L 未加载"));
        factoryCalled.Should().BeFalse("前置门禁未过不得构造 transport");
        vm.IsAcquiring.Should().BeFalse();
    }

    [Fact]
    public async Task Start_fails_when_no_channel_selected()
    {
        var connection = ConnectionPanel(out _);
        connection.SelectedChannel = null;

        var vm = NewVm(connection, new ScriptedTransport());

        (await vm.StartAsync()).Should().BeFalse();
        vm.StatusLines.Should().Contain(l => l.Contains("未选择通道"));
    }

    [Fact]
    public async Task Start_fails_when_selected_channel_has_no_icanchannel()
    {
        var source = new ConnectedChannelsSource();
        source.Publish(new[]
        {
            new HilViewModel.ConnectedChannel(0x51, BaudRate.Can500kbps, Fd: false, "USB1", Channel: null),
        });
        var connection = new XcpConnectionPanelViewModel(loadA2l: _ => DefaultLoaded(), connectedChannels: source);
        connection.A2lPath = TempA2lFile();
        connection.LoadA2LCommand.Execute(null);
        connection.SelectedChannel = source.Current[0];

        var vm = new XcpAcquisitionPanelViewModel(connection: connection, transportFactory: _ => new ScriptedTransport());

        (await vm.StartAsync()).Should().BeFalse();
        vm.StatusLines.Should().Contain(l => l.Contains("ICanChannel"));
    }

    [Fact]
    public async Task Start_fails_when_a2l_has_no_xcp_on_can_block()
    {
        var source = new ConnectedChannelsSource();
        source.Publish(new[]
        {
            new HilViewModel.ConnectedChannel(0x51, BaudRate.Can500kbps, Fd: false, "USB1", Channel: new FakeChannel()),
        });
        var connection = new XcpConnectionPanelViewModel(
            loadA2l: _ => DefaultLoaded(withOnCan: false), connectedChannels: source);
        connection.A2lPath = TempA2lFile();
        connection.LoadA2LCommand.Execute(null);
        connection.SelectedChannel = source.Current[0];

        var transport = new ScriptedTransport();
        var vm = new XcpAcquisitionPanelViewModel(connection: connection, transportFactory: _ => transport);

        (await vm.StartAsync()).Should().BeFalse("CAN ID 无声明侧来源，按声明值走不猜");
        vm.StatusLines.Should().Contain(l => l.Contains("XCP_ON_CAN"));
        transport.SentPids.Should().BeEmpty("未进对账就拒绝，不得打任何帧");
        vm.CapabilityReport.Should().BeNull("未到对账步骤，不得留下误导性报告");
    }

    [Fact]
    public async Task Start_fails_when_transport_write_fails_and_disposes_transport()
    {
        var connection = ConnectionPanel(out _);
        var transport = new ScriptedTransport { ThrowOnWrite = true };
        var vm = NewVm(connection, transport);

        (await vm.StartAsync()).Should().BeFalse();
        vm.StatusLines.Should().Contain(l => l.Contains("无法启动采集"));
        transport.Disposed.Should().BeTrue("失败路径必须释放已构造的 transport");
        vm.CapabilityReport.Should().BeNull("CONNECT 都发不出去，无对账报告可言");
        vm.IsAcquiring.Should().BeFalse();
    }

    [Fact]
    public async Task Start_fails_when_transport_factory_throws()
    {
        var connection = ConnectionPanel(out _);
        var vm = new XcpAcquisitionPanelViewModel(
            connection: connection,
            transportFactory: _ => throw new InvalidOperationException("factory broken"));

        (await vm.StartAsync()).Should().BeFalse();
        vm.StatusLines.Should().Contain(l => l.Contains("会话构造失败"));
        vm.IsAcquiring.Should().BeFalse();
    }

    [Fact]
    public async Task Plan_failure_is_not_silent_and_disposes_session()
    {
        // 双模块文档：AcquisitionPlanner 单模块口径 fail-loud（S2-T9）。
        var source = new ConnectedChannelsSource();
        source.Publish(new[]
        {
            new HilViewModel.ConnectedChannel(0x51, BaudRate.Can500kbps, Fd: false, "USB1", Channel: new FakeChannel()),
        });
        var connection = new XcpConnectionPanelViewModel(
            loadA2l: _ => new XcpA2lLoadResult.Loaded(
                SmallDoc(modules: 2), ModuleIfData(), Array.Empty<ValidationNote>(),
                new ContractSet(SmallDoc(modules: 2))),
            connectedChannels: source);
        connection.A2lPath = TempA2lFile();
        connection.LoadA2LCommand.Execute(null);
        connection.SelectedChannel = source.Current[0];

        var transport = new ScriptedTransport();
        var vm = new XcpAcquisitionPanelViewModel(connection: connection, transportFactory: _ => transport);

        (await vm.StartAsync()).Should().BeFalse("规划失败必须拒绝启动，不准静默降级");
        vm.StatusLines.Should().Contain(l => l.Contains("无法启动采集"));
        vm.CoverageEntries.Should().BeEmpty("未 Plan 成功不得展示覆盖清单");
        transport.Disposed.Should().BeTrue();
        vm.IsAcquiring.Should().BeFalse();
    }

    [Fact]
    public async Task Reconcile_rejects_download_declared_but_never_probed()
    {
        var connection = ConnectionPanelWithLoaded(DefaultLoaded(declaresDownload: true), out _);
        var transport = new ScriptedTransport();
        var vm = NewVm(connection, transport);

        (await vm.StartAsync()).Should().BeFalse();
        vm.CapabilityReport!.RejectedStart.Should().BeTrue();
        vm.CapabilityReport.Findings.Should().Contain(f =>
            f.Code == "COMMAND_DECLARED_NOT_MEASURED" && f.Message.Contains("0xF0"));
        // DOWNLOAD 零入口（spec §1/T10 守卫）：全流程零 0xF0 帧，宁可拒绝不探测写命令。
        transport.SentPids.Should().NotContain(XcpPid.Download);
    }

    [Fact]
    public async Task Capability_query_failure_rejects_start_instead_of_silent_start()
    {
        var connection = ConnectionPanel(out _);
        var transport = new ScriptedTransport();
        transport.Silence(XcpPid.GetDaqProcessorInfo);
        var vm = new XcpAcquisitionPanelViewModel(
            connection: connection,
            transportFactory: _ => transport,
            // 快 T1：实测缺失走对账拒绝，而不是等默认 2s×2 重试。
            masterOptionsOverride: new XcpMasterOptions(
                new CanId(0x18FFF667, FrameFormat.Extended),
                timeout: TimeSpan.FromMilliseconds(50), maxRetries: 0));

        (await vm.StartAsync()).Should().BeFalse("硬约束字段实测缺失/失配必须拒绝");
        vm.CapabilityReport!.RejectedStart.Should().BeTrue();
        transport.SentPids.Count(p => p == XcpPid.GetDaqProcessorInfo).Should().Be(1, "maxRetries=0");
        transport.SentPids.Count(p => p == XcpPid.StartStopDaqList).Should().Be(1, "对账拒绝后不得配置轮转（探测链继续是 probe 语义）");
    }

    [Fact]
    public async Task Reconciliation_warnings_surface_into_status_area()
    {
        var note = new ValidationNote(
            CrossCheckKind.MissingAddress, "fixture warning", NoteLines, ErrorSeverity.Warning);
        var connection = ConnectionPanelWithLoaded(DefaultLoaded(notes: new[] { note }), out _);
        var transport = new ScriptedTransport();
        var vm = NewVm(connection, transport, FastPollingOptions());

        (await vm.StartAsync()).Should().BeTrue("告警不阻断启动，但必须浮出");
        vm.StatusLines.Should().Contain(l => l.Contains("[Warning]") && l.Contains("A2L_CROSSCHECK"));
        vm.CapabilityReport!.RejectedStart.Should().BeFalse();
        await vm.StopAsync();
    }

    [Fact]
    public async Task StartCommand_canexecute_tracks_isacquiring_gate()
    {
        var connection = ConnectionPanel(out _);
        var vm = NewVm(connection, new ScriptedTransport(), FastPollingOptions());

        vm.StartCommand.CanExecute(null).Should().BeTrue("空闲时允许 Start");

        (await vm.StartAsync()).Should().BeTrue();
        vm.StartCommand.CanExecute(null).Should().BeFalse("运行中禁止重 Start（IsAcquiring 门禁）");

        await vm.StopAsync();
        vm.StartCommand.CanExecute(null).Should().BeTrue("Stop 后允许再次 Start");
    }

    [Fact]
    public async Task Stop_twice_in_a_row_is_idempotent()
    {
        var connection = ConnectionPanel(out _);
        var transport = new ScriptedTransport();
        var vm = NewVm(connection, transport, FastPollingOptions());

        (await vm.StartAsync()).Should().BeTrue();
        await vm.StopAsync();
        var statusAfterFirstStop = vm.StatusLines.ToArray();

        await vm.StopAsync();

        vm.IsAcquiring.Should().BeFalse();
        vm.StatusLines.Should().Equal(statusAfterFirstStop, "重复 Stop 是 no-op，不改写状态区");
        transport.Disposed.Should().BeTrue();
        connection.ConnectionState.Should().Be(XcpConnectionState.Loaded);
    }

    [Fact]
    public async Task Master_frames_use_declared_can_id()
    {
        var connection = ConnectionPanel(out _);
        var transport = new ScriptedTransport();
        var vm = NewVm(connection, transport, FastPollingOptions());

        (await vm.StartAsync()).Should().BeTrue();

        transport.SentFrames.Should().OnlyContain(f =>
            f.Id.Raw == 0x18FFF667 && f.Id.Format == FrameFormat.Extended,
            "主站发送 CAN ID 按声明值走（XCP_ON_CAN.MASTER_CAN_ID）");
        await vm.StopAsync();
    }

    [Fact]
    public async Task Stop_without_start_never_constructs_transport()
    {
        var connection = ConnectionPanel(out _);
        var factoryCalls = 0;
        var vm = new XcpAcquisitionPanelViewModel(
            connection: connection,
            transportFactory: _ => { factoryCalls++; return new ScriptedTransport(); });

        await vm.StopAsync();

        factoryCalls.Should().Be(0, "未启动过的 Stop 是纯 no-op");
        vm.IsAcquiring.Should().BeFalse();
    }
}









