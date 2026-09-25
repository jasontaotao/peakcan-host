using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using A2lEditor.Core;
using A2lEditor.Core.IfData;
using A2lEditor.Core.Layout;
using A2lEditor.Core.Model;
using FluentAssertions;
using NSubstitute;
using PeakCan.HIL.Core;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.App.ViewModels.Xcp;
using PeakCan.Host.App.Views.Xcp;
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
/// S3-T11 E2E（spec §4 验收 1 模拟档）：mock transport（T7 ScriptedTransport 形状精简复制）
/// 注入 → 连接面板 LoadA2L（最小 A2L 模型经 loadA2l 委托）→ StartAsync 全链
///（对账 → Plan → ConfigureRotation → 轮询）→ 模拟从机 DAQ DTO 注入 → sink 出样本 →
/// 卡片 Flush 更新 → StopAsync 静默（接收链拆除后注入不再上卡）。
/// <para>
/// 另钉 T9 评审 M-1 选择器接线语义：ConfirmedRows → XcpView.ApplyConfirmedRows
/// 按 (name, category) 扫 <see cref="ContractSet.All"/> 对查 contract（禁按名索引），
/// 同名异类第二行被卡片 AddWatch 按名去重拒绝（取舍见 spec 已知限制清单）。
/// </para>
/// </summary>
public class XcpWiringE2ETests : IDisposable
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

    // ------------------------------------------------------------------
    // 夹具（T7 形状精简：单测量对象最小链）
    // ------------------------------------------------------------------

    private string TempA2lFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"xcp-t11-e2e-{Guid.NewGuid():N}.a2l");
        File.WriteAllText(path, "ASAP2_VERSION 1 40\n");
        _tempFiles.Add(path);
        return path;
    }

    /// <summary>声明 OPTIONAL_CMD（与实测链一一对应；不含 DOWNLOAD——本链不声明则不探测）。</summary>
    private static readonly string[] DeclaredOptionalCommands =
    [
        "GET_COMM_MODE_INFO", "SET_MTA", "UPLOAD", "SHORT_UPLOAD",
        "SET_DAQ_PTR", "WRITE_DAQ", "CLEAR_DAQ_LIST",
        "START_STOP_DAQ_LIST", "START_STOP_SYNCH",
        "GET_DAQ_PROCESSOR_INFO", "GET_DAQ_RESOLUTION_INFO",
        "GET_DAQ_LIST_INFO", "GET_DAQ_EVENT_INFO",
    ];

    /// <summary>单测量对象（EngineSpeed，1B UBYTE @0x1000，段映射覆盖）的最小文档。</summary>
    private static A2lDocument SingleMeasurementDoc() => DocWith(
        new[] { Meas("EngineSpeed", 0x1000) }, Array.Empty<A2lCharacteristic>());

    /// <summary>同名异类文档（T9 M-1）：Rpm/MEASUREMENT + Rpm/CHARACTERISTIC。</summary>
    private static A2lDocument SameNameDoc() => DocWith(
        new[] { Meas("Rpm", 0x1000) },
        new[] { new A2lCharacteristic("Rpm", "d", "VALUE", "RL_U8", 0x1001, "0", "100", null,
            "CM_ID", new LineRange(1, 1)) });

    private static A2lMeasurement Meas(string name, ulong addr) =>
        new(name, "d", A2lDataType.UBYTE, "CM_ID", "0", "0", "0", "65535", addr, new LineRange(1, 1));

    private static A2lDocument DocWith(
        A2lMeasurement[] measurements, A2lCharacteristic[] characteristics)
    {
        var cmIdentical = new A2lCompuMethod("CM_ID", "id", "IDENTICAL", "%d", "-",
            new IdenticalConversion(), new LineRange(1, 1));
        var layout = new A2lRecordLayout("RL_U8",
            new[] { new RecordLayoutEntry("FNC_VALUES", 0, "UBYTE", "COLUMN_DIR", "DIRECT", null, null) },
            new LineRange(1, 1));
        var memorySegment = new A2lMemorySegment("CAL", "cal", "DATA", "FLASH", "INTERN",
            0x1000, 0x1000, Array.Empty<string>(), new LineRange(1, 1),
            IfDataXcp: SegmentIfData());

        var module = new A2lModule("M", "m",
            measurements, characteristics, Array.Empty<A2lAxisPts>(),
            new[] { cmIdentical }, new[] { layout }, Array.Empty<A2lGroup>(), null,
            Array.Empty<A2lAxisDescr>(), Array.Empty<A2lUserRights>(),
            Array.Empty<A2lVersionInfo>(), Array.Empty<A2lAxisPtsX>(),
            new LineRange(1, 1), MemorySegments: new[] { memorySegment },
            IfDataXcp: ModuleIfData());
        return new A2lDocument(A2lVersion.V1_6x, "P", "", "",
            new A2lModCommon("", A2lByteOrder.MSB_LAST, null, null, null, new LineRange(1, 1)),
            new[] { module }, "", 1);
    }

    private static XcpIfData ModuleIfData()
    {
        var protocolLayer = new XcpProtocolLayer(
            SourceVersion: "1.0", XcpVersion: 0x0100,
            T1Ms: 2000, T2Ms: 20, T3Ms: 20, T4Ms: 1000, T5Ms: 10, T6Ms: 10, T7Ms: 2000,
            MaxCto: 8, MaxDto: 8,
            ByteOrder: XcpByteOrder.MsbLast, AddressGranularity: XcpAddressGranularity.Byte,
            OptionalCommands: DeclaredOptionalCommands,
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
            new[] { onCan }, Array.Empty<XcpSegment>(),
            Array.Empty<A2lUnknownBlock>(), Array.Empty<XcpMissingField>(), "");
    }

    /// <summary>段级 IF_DATA：逻辑 0x1000 起 0x1000 长的地址映射。</summary>
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

    private static XcpA2lLoadResult.Loaded DefaultLoaded(A2lDocument doc) =>
        new(doc, ModuleIfData(), Array.Empty<ValidationNote>(), new ContractSet(doc));

    private XcpConnectionPanelViewModel ConnectionPanelWithLoaded(
        XcpA2lLoadResult.Loaded loaded)
    {
        var source = new ConnectedChannelsSource();
        source.Publish(new[]
        {
            new HilViewModel.ConnectedChannel(0x51, BaudRate.Can500kbps, Fd: false, "USB1",
                Channel: new FakeChannel()),
        });
        var connection = new XcpConnectionPanelViewModel(loadA2l: _ => loaded, connectedChannels: source);
        connection.A2lPath = TempA2lFile();
        connection.LoadA2LCommand.Execute(null);
        connection.SelectedChannel = source.Current[0];
        return connection;
    }

    private static XcpAcquisitionPanelViewModel NewAcquisition(
        XcpConnectionPanelViewModel connection,
        XcpCardPanelViewModel cards,
        ScriptedTransport transport) =>
        new(
            connection: connection,
            sink: cards.Sink,
            transportFactory: _ => transport,
            sessionOptions: new XcpAcquisitionSessionOptions
            {
                Polling = new PollingSchedulerOptions { Period = TimeSpan.FromMilliseconds(5) },
            });

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        for (var elapsed = 0; elapsed < timeoutMs && !condition(); elapsed += 10)
            await Task.Delay(10);
        Assert.True(condition(), "condition not met within timeout");
    }

    // ------------------------------------------------------------------
    // E2E 全链：LoadA2L → Start（对账/Plan/轮转/轮询）→ DTO 注入 → Flush 上卡 → Stop 静默
    // ------------------------------------------------------------------

    [Fact]
    public async Task Full_chain_load_reconcile_start_sample_flush_stop()
    {
        var connection = ConnectionPanelWithLoaded(DefaultLoaded(SingleMeasurementDoc()));
        var transport = new ScriptedTransport();
        var cards = new XcpCardPanelViewModel();
        var acquisition = NewAcquisition(connection, cards, transport);

        // 选择器接线（T11）：ConfirmedRows → (name, category) 扫 Contracts.All → AddWatch。
        XcpView.ApplyConfirmedRows(cards, connection.LoadedResult!.Contracts,
            new[] { new XcpWatchRow("EngineSpeed", "MEASUREMENT") });
        cards.Cards.Should().ContainSingle(c => c.Name == "EngineSpeed");

        (await acquisition.StartAsync()).Should().BeTrue("黄金样本从机声明一致，对账放行");
        acquisition.IsAcquiring.Should().BeTrue();
        connection.ConnectionState.Should().Be(XcpConnectionState.Connected, "对账通过后 MarkConnected 被调");

        // 模拟从机 DAQ DTO 流（100 Hz 注入路径）：首字节 ODT PID=0，随后 1B UBYTE 样本。
        transport.InjectDto(0x00, 0x42);
        await WaitUntilAsync(() => cards.Sink.Count > 0);
        cards.Flush();
        var card = cards.Cards.Single(c => c.Name == "EngineSpeed");
        card.DisplayValue.Should().Be("66", "0x42 UBYTE 恒等转换按合同 FORMAT 渲染");
        card.LastUpdate.Should().NotBeNull();

        transport.InjectDto(0x00, 0x43);
        await WaitUntilAsync(() => cards.Sink.Count > 0);
        cards.Flush();
        card.DisplayValue.Should().Be("67", "100 Hz 节拍的后续样本被 Flush 批量上卡");

        // 覆盖清单（E2E 兼钉）：planner 承接的覆盖行已进 UI 绑定集合。
        acquisition.CoverageEntries.Should().ContainSingle(r =>
            r.ObjectName == "EngineSpeed" && r.Covered && r.PollingCause == null && r.MissingCause == null);
        // Stop 全链路：await 会话静默 → Dispose → 回 Loaded。
        await acquisition.StopAsync();
        transport.Disposed.Should().BeTrue();
        connection.ConnectionState.Should().Be(XcpConnectionState.Loaded);

        // StopAsync 静默口径：接收链已拆除，后续注入不再解码/上卡。
        transport.InjectDto(0x00, 0x7F);
        await Task.Delay(50);
        cards.Flush();
        card.DisplayValue.Should().Be("67", "Stop 后 sink 无新样本（会话已释放）");
        cards.Sink.Count.Should().Be(0);
    }

    [Fact]
    public async Task Restart_after_stop_does_not_require_reloading_a2l()
    {
        var connection = ConnectionPanelWithLoaded(DefaultLoaded(SingleMeasurementDoc()));
        var transport = new ScriptedTransport();
        var cards = new XcpCardPanelViewModel();
        var acquisition = NewAcquisition(connection, cards, transport);

        (await acquisition.StartAsync()).Should().BeTrue();
        await acquisition.StopAsync();
        connection.ConnectionState.Should().Be(XcpConnectionState.Loaded);

        // D6/T3 MEDIUM-1：Stop 后 A2L 仍有效，重连不需要重载（无"必须重新 LoadA2L"死胡同）。
        (await acquisition.StartAsync()).Should().BeTrue("Loaded 态可直接再次 Start");
        connection.ConnectionState.Should().Be(XcpConnectionState.Connected);
        await acquisition.StopAsync();
    }
    // ------------------------------------------------------------------
    // T9 M-1：选择器确认行 → (name, category) 对查合同 → 同名异类卡片去重取舍
    // ------------------------------------------------------------------

    [Fact]
    public void Apply_confirmed_rows_twice_is_idempotent_on_cards()
    {
        var contracts = new ContractSet(SingleMeasurementDoc());
        var cards = new XcpCardPanelViewModel();
        var rows = new[] { new XcpWatchRow("EngineSpeed", "MEASUREMENT") };

        XcpView.ApplyConfirmedRows(cards, contracts, rows);
        XcpView.ApplyConfirmedRows(cards, contracts, rows);

        cards.Cards.Should().ContainSingle("二次确认经卡片按名去重，不产生重复卡片");
    }
    [Fact]
    public void Picker_confirmed_rows_use_name_category_lookup_and_cards_dedup_by_name()
    {
        var doc = SameNameDoc();
        var contracts = new ContractSet(doc);
        var session = Substitute.For<ITraceSessionService>();
        session.XcpWatchedObjects.Returns(new ObservableCollection<XcpWatchRow>());

        var picker = new XcpObjectPickerViewModel(contracts, session);
        foreach (var group in picker.Roots)
        foreach (var leaf in group.Children.Where(n => n.Name == "Rpm"))
            leaf.IsSelected = true;

        var rows = picker.Confirm();
        rows.Should().HaveCount(2, "picker 按 (name, category) 允许同名异类同存");
        session.XcpWatchedObjects.Should().HaveCount(2, "关注集按对去重——同名异类两行都进");

        var cards = new XcpCardPanelViewModel();
        XcpView.ApplyConfirmedRows(cards, contracts, rows);

        cards.Cards.Should().ContainSingle(
            "卡片 AddWatch 按名去重——同名异类第二行被拒绝（取舍见 spec 已知限制清单）");
        var card = cards.Cards.Single();
        card.Name.Should().Be("Rpm");
        card.Category.Should().Be("MEASUREMENT");
        var measurement = contracts.All.Should().ContainSingle(c =>
            c.ObjectName == "Rpm" && c.Category == A2lObjectCategory.Measurement).Subject;
        card.Contract.Should().BeSameAs(measurement, "按 (name, category) 对查命中测量合同，不走按名索引");
    }

    [Fact]
    public void Confirmed_rows_without_matching_contract_pair_are_skipped()
    {
        var contracts = new ContractSet(SingleMeasurementDoc());
        var cards = new XcpCardPanelViewModel();

        // A2L 换版后的关注集残留行（声明面无此对）→ 跳过不炸，不产生卡片。
        XcpView.ApplyConfirmedRows(cards, contracts,
            new[] { new XcpWatchRow("Gone", "MEASUREMENT") });

        cards.Cards.Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // 传输替身（T7 ScriptedTransport 精简复制 + DTO 注入口）
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
        private readonly List<byte> _sentPids = new();

        private event Action<CanFrame>? _frameReceived;
        public event Action<CanFrame>? FrameReceived
        {
            add => _frameReceived += value;
            remove => _frameReceived -= value;
        }

        public long FramesDropped => 0;
        public bool Disposed { get; private set; }

        public IReadOnlyList<byte> SentPids
        {
            get { lock (_gate) return _sentPids.ToArray(); }
        }

        /// <summary>模拟从机 DAQ DTO（spec §4 验收 1 注入路径）：PID + 数据场。</summary>
        public void InjectDto(byte pid, params byte[] payload)
        {
            var data = new byte[1 + payload.Length];
            data[0] = pid;
            Array.Copy(payload, 0, data, 1, payload.Length);
            _frameReceived?.Invoke(new CanFrame(
                SlaveCanId,
                new ReadOnlyMemory<byte>(data),
                FrameFlags.None, ChannelId.None, default));
        }

        public async ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
        {
            var pid = frame.Data.Span[0];
            byte[] response;
            lock (_gate)
            {
                _sentPids.Add(pid);
                response = GoldenResponses.TryGetValue(pid, out var golden)
                    ? golden
                    : new byte[] { XcpPid.PositiveResponse };
            }

            _frameReceived?.Invoke(new CanFrame(
                SlaveCanId,
                new ReadOnlyMemory<byte>(response),
                FrameFlags.None, ChannelId.None, default));
            return await ValueTask.FromResult(Result<Unit>.Ok(default));
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
}