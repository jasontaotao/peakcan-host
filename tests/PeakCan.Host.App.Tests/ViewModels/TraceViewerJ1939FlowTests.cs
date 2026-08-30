using System.Collections.ObjectModel;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.J1939;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.ViewModels;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.J1939;
using PeakCan.HIL.Core.Replay;
using ScottPlot;
using Xunit;

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
    public static TraceViewerViewModel Create(ITraceViewerService master, string sourceId = "a")
        => CreateWithRegistry(master, sourceId).Vm;

    // Task 13：卸载路径用例需要持有 registry 以清空 Sources + 触发 SourcesChanged。
    public static (TraceViewerViewModel Vm, ITraceSessionRegistry Registry) CreateWithRegistry(
        ITraceViewerService master, string sourceId = "a")
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
        // 真 DbcService 空实例（未加载 DBC）。
        var dbcService = new DbcService(Substitute.For<ILogger<DbcService>>());
        var sessionLibrary = new TraceSessionLibrary(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tmtrace-j1939-{Guid.NewGuid():N}.tmtrace"),
            NullLogger<TraceSessionLibrary>.Instance);
        var vm = new TraceViewerViewModel(
            session, registry, dbcService, NullLogger<TraceViewerViewModel>.Instance, sessionLibrary,
            j1939Reassembly: new J1939ReassemblyService());
        return (vm, registry);
    }
}
