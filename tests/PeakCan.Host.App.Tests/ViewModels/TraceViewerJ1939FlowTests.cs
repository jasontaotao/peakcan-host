using System.Collections.ObjectModel;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.ChatTools;
using PeakCan.Host.App.Services.J1939;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.ViewModels;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.J1939;
using PeakCan.HIL.Core.Replay;
using ScottPlot;
using Xunit;
using DbcValueType = PeakCan.HIL.Core.Dbc.ValueType;

namespace PeakCan.Host.App.Tests.ViewModels;

public class TraceViewerJ1939FlowTests
{
    private static ReplayFrame CmBam(double ts) => new(
        ts, J1939Id.Compose(6, 0x00EC00, 0xF4, 0xFF), 8,
        TpCmMessage.Bam(49, 7, 0x000200).Encode(), FrameFlags.None, true);

    private static ReplayFrame Dt(byte seq, double ts, byte[] data) => new(
        ts, J1939Id.Compose(6, 0x00EB00, 0xF4, 0xFF), 8,
        new TpDtMessage(seq, data).Encode(), FrameFlags.None, true);

    private static List<ReplayFrame> BuildBamFrames()
    {
        var frames = new List<ReplayFrame> { CmBam(1.0) };
        for (byte i = 1; i <= 7; i++)
        {
            int take = Math.Min(7, 49 - (i - 1) * 7);
            var chunk = new byte[take];
            for (int j = 0; j < take; j++) chunk[j] = (byte)(i * 7 + j);
            frames.Add(Dt(i, 1.0 + i * 0.01, chunk));
        }
        return frames;
    }

    [Fact]
    public void RebuildJ1939Views_Populates_ReassembledMessages_And_DecodeFrames()
    {
        var master = Substitute.For<ITraceViewerService>();
        var frames = BuildBamFrames();
        master.LoadedFrames.Returns(frames);
        var vm = TraceViewerViewModelFactory.Create(master);   // 见下方工厂说明
        vm.ReassembledMessages.Should().BeEmpty();

        vm.RebuildJ1939ViewsCommand.Execute(null);

        vm.ReassembledMessages.Should().ContainSingle().Which.Status.Should().Be(ReassemblyStatus.Complete);
        vm.DecodeFrames.Should().HaveCount(frames.Count + 1);  // 原始 8 帧 + 1 虚拟帧
    }

    [Fact]
    public void RefreshFrameCounts_Includes_J1939_Virtual_Frames_For_Multiframe_Signal()
    {
        // 回归：多帧报文（BRM）在原始帧里只有 TP.CM/TP.DT，完整 49B 载荷仅存在于
        // 重组虚拟帧。修复前 RefreshFrameCounts 只分组 registry 原始帧 → watch 行
        // FrameCount 恒 0、LatestValue 恒 NaN → 多帧信号在 watch list 取不到。
        // DBC BRM ID = 0x9802FFF4（bit31 惯例）与虚拟帧 0x1802FFF4 精确匹配。
        var master = Substitute.For<ITraceViewerService>();
        master.LoadedFrames.Returns(BuildBamFrames());
        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        dbc.SetCurrentForTests(DocWithBrm());
        var vm = TraceViewerViewModelFactory.Create(master, dbcService: dbc);

        vm.RebuildJ1939ViewsCommand.Execute(null);
        vm.DecodeFrames.Should().HaveCount(9);   // 8 原始 + 1 虚拟帧

        vm.AddToWatch(0x9802FFF4u, "SOC", "");
        var row = vm.WatchedSignals.Single(w => !w.IsPlaceholder);
        row.FrameCount.Should().Be(1);      // 仅虚拟帧命中（原始帧里无完整 BRM）
        row.LatestValue.Should().Be(7.0);   // 重组 payload 首字节 = 7（BuildBamFrames seq1）
    }

    // BAM 广播虚拟帧 ID = Compose(6, 0x000200, 0xF4, 0xFF) = 0x1802FFF4；
    // DBC 惯例 A：bit31 置位（0x9C... 同款），masked 后精确匹配。
    private static DbcDocument DocWithBrm()
    {
        var msg = new Message(
            Id: 0x9802FFF4u, Name: "BRM", Dlc: 49, Sender: "BMS",
            Signals: new[] { new Signal("SOC", 0, 8, ByteOrder.LittleEndian, DbcValueType.Unsigned, 1.0, 0.0, 0, 100, "%", Array.Empty<string>()) },
            IsMultiplexed: false, MultiplexorSignalIndex: null);
        return new DbcDocument(
            Version: "1.0", Nodes: Array.Empty<Node>(),
            Messages: new[] { msg },
            MessagesById: new Dictionary<uint, Message> { [0x9802FFF4u] = msg },
            ValueTables: new Dictionary<string, ValueTable>());
    }

    [Fact]
    public void BlueAnchor_Value_Includes_J1939_Virtual_Frames_For_Multiframe_Signal()
    {
        // 回归：蓝色锚线取值帧源走 registry 原始帧（多帧报文只有 TP.CM/TP.DT），
        // BRM 信号 BlueLatestValue 恒 NaN。锚线须与图表/watch 同源（DecodeFrames，
        // 含重组虚拟帧）才能取到多帧载荷。
        var master = Substitute.For<ITraceViewerService>();
        master.LoadedFrames.Returns(BuildBamFrames());
        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        dbc.SetCurrentForTests(DocWithBrm());
        var vm = TraceViewerViewModelFactory.Create(master, dbcService: dbc);

        vm.RebuildJ1939ViewsCommand.Execute(null);
        vm.AddToWatch(0x9802FFF4u, "SOC", "");

        vm.RefreshAtAnchorBlue(1.1);   // 最近帧 = 完成时刻 1.07 的虚拟帧

        var row = vm.WatchedSignals.Single(w => !w.IsPlaceholder);
        row.BlueLatestValue.Should().Be(7.0);   // 重组 payload 首字节
        row.BlueFrameCount.Should().Be(1);
    }

    [Fact]
    public void ChatToolContext_GetFrames_Includes_J1939_Virtual_Frames_For_Multiframe_Signal()
    {
        // 回归：IChatToolContext.GetFrames 供 get_signal_overview / search_signal_trace /
        // anomaly_scan / analyze_timing_sequence 四工具取值。修复前直接返回 registry
        // 原始帧（多帧报文只有 TP.CM/TP.DT）→ BRM 信号按 DBC ID 过滤零命中 →
        // AI Chat 对多帧信号报 "no frames"。必须并入重组虚拟帧（同 RefreshFrameCounts）。
        var master = Substitute.For<ITraceViewerService>();
        master.LoadedFrames.Returns(BuildBamFrames());
        var vm = TraceViewerViewModelFactory.Create(master);
        vm.RebuildJ1939ViewsCommand.Execute(null);

        IChatToolContext ctx = vm;
        var frames = ctx.GetFrames("a");

        frames.Should().Contain(f => (f.Id & 0x7FFFFFFFu) == 0x1802FFF4u);   // BRM 虚拟帧
    }

    [Fact]
    public void BlueAnchor_Sorts_Mixed_Original_And_Virtual_Frames_For_Same_CanId()
    {
        // review F1：同一 maskedId 同时存在原始单帧（0.5/2.0s）与重组虚拟帧（1.07s）——
        // 同一 PGN ≤8B 直接发 + >8B 走 TP 的合法混发。锚线合并若不做 OrderBy，
        // filtered 序列 [0.5, 2.0, 1.07] 破坏二分前提，锚定 1.5s 会错选 2.0s 原始帧
        // （SOC=3.0）而非最近的 1.07s 虚拟帧（SOC=7.0）。
        var master = Substitute.For<ITraceViewerService>();
        var raw = BuildBamFrames();
        raw.Add(new ReplayFrame(0.5, 0x1802FFF4u, 2, new byte[] { 1, 2 }, FrameFlags.None, true));
        raw.Add(new ReplayFrame(2.0, 0x1802FFF4u, 2, new byte[] { 3, 4 }, FrameFlags.None, true));
        master.LoadedFrames.Returns(raw);
        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        dbc.SetCurrentForTests(DocWithBrm());
        var vm = TraceViewerViewModelFactory.Create(master, dbcService: dbc);

        vm.RebuildJ1939ViewsCommand.Execute(null);
        vm.AddToWatch(0x9802FFF4u, "SOC", "");
        vm.RefreshAtAnchorBlue(1.5);

        var row = vm.WatchedSignals.Single(w => !w.IsPlaceholder);
        row.BlueLatestValue.Should().Be(7.0);   // 1.07 虚拟帧，而非 2.0 原始帧的 3.0
    }

    [Fact]
    public void SeekToReassembled_Seeks_FirstFrame_Timestamp()
    {
        var master = Substitute.For<ITraceViewerService>();
        master.LoadedFrames.Returns(BuildBamFrames());
        var vm = TraceViewerViewModelFactory.Create(master);
        vm.RebuildJ1939ViewsCommand.Execute(null);

        vm.SeekToReassembledCommand.Execute(vm.ReassembledMessages[0]);

        master.Received(1).Seek(1.0);   // TP.CM 首帧时间（spec §9.2）
    }

    // Task 13 路由决策（stale-DecodeFrames）：最后一个源卸载后 _masterService 恒为
    // null，若重建命令对无源状态只 early-return，陈旧虚拟帧会永久滞留在 DecodeFrames。
    // 钉住"卸载 → 经现有重建路径清空 L2 行 + 虚拟帧输入 → DecodeFrames 退化为原始帧"。
    [Fact]
    public void Unload_Last_Source_Clears_Panel_And_DecodeFrames_Degrades_To_Raw()
    {
        var master = Substitute.For<ITraceViewerService>();
        master.LoadedFrames.Returns(BuildBamFrames());
        var (vm, registry) = TraceViewerViewModelFactory.CreateWithRegistry(master);
        vm.RebuildJ1939ViewsCommand.Execute(null);
        vm.ReassembledMessages.Should().ContainSingle();
        vm.DecodeFrames.Should().HaveCount(9);   // 原始 8 帧 + 1 虚拟帧

        // 最后一个源卸载（RemoveTraceAsync → registry.UnloadAsync → SourcesChanged 同路径）。
        registry.Sources.Returns(new List<TraceSource>());
        registry.SourcesChanged += Raise.Event<Action>();

        vm.ReassembledMessages.Should().BeEmpty();
        vm.DecodeFrames.Should().BeEmpty();   // master null → 空序列（不再吃陈旧合并列表）
    }
}

/// <summary>
/// 测试工厂（brief Task 12 Step 1 工厂说明）：App.Tests 已有的 TraceViewerViewModel
/// 构造先例（ChatFlowTests.BuildVm / TraceViewerViewModelRebuildSignalsTests）照抄——
/// NSubstitute mock 接口、NullLogger、真 DbcService 空实例；master service 经
/// registry 单源绑定（Sources 一条 + GetService 返回 master，ctor 初始拉取即绑定，
/// 不触发任何加载路径 → ReassembledMessages 保持空）。
/// </summary>
internal static class TraceViewerViewModelFactory
{
    public static TraceViewerViewModel Create(ITraceViewerService master, string sourceId = "a", DbcService? dbcService = null)
        => CreateWithRegistry(master, sourceId, dbcService).Vm;

    // Task 13：卸载路径用例需要持有 registry 以清空 Sources + 触发 SourcesChanged。
    public static (TraceViewerViewModel Vm, ITraceSessionRegistry Registry) CreateWithRegistry(
        ITraceViewerService master, string sourceId = "a", DbcService? dbcService = null)
    {
        // 配置非空集合，否则 VM ctor 对 WatchedSignals.CollectionChanged 的订阅会 NRE
        //（ChatFlowTests.BuildVm 同款注释）。
        var session = Substitute.For<ITraceSessionService>();
        session.WatchedSignals.Returns(new ObservableCollection<WatchedSignalRow>());
        session.SignalGroups.Returns(new ObservableCollection<WatchedSignalGroup>());
        var registry = Substitute.For<ITraceSessionRegistry>();
        registry.Sources.Returns(new List<TraceSource>
        {
            new(sourceId, "traceA", "C:/traceA.asc", Colors.Blue, new LineStyle()),
        });
        registry.GetService(sourceId).Returns(master);
        // 真 DbcService 空实例（未加载 DBC）；测试可注入带 DBC 的服务。
        var dbc = dbcService ?? new DbcService(Substitute.For<ILogger<DbcService>>());
        var sessionLibrary = new TraceSessionLibrary(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tmtrace-j1939-{Guid.NewGuid():N}.tmtrace"),
            NullLogger<TraceSessionLibrary>.Instance);
        var vm = new TraceViewerViewModel(
            session, registry, dbc, NullLogger<TraceViewerViewModel>.Instance, sessionLibrary,
            j1939Reassembly: new J1939ReassemblyService());
        return (vm, registry);
    }
}
