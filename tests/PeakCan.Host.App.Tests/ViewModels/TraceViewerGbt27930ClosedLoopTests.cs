// CLOSED-LOOP TEST: 真实 GB/T 27930 充电 log → J1939 重组 → AI Chat 工具取多帧。
//
// 覆盖此前缺失的闭环：现有 J1939ReassemblyServiceTests 用合成帧、ChatFlowTests 用
// FakeChatTool、ChatToolContext_GetFrames 测试用 BuildBamFrames 合成帧——没有一条
// 把「真实 27930 log 导入 → 重组虚拟帧 → 真实 chat 工具解码多帧信号」串起来。
// 本文件用用户提供的真实 log 截段 (5 个完整 RTS/CTS BRM 会话) + 配套 DBC，
// 直连真实工具 ExecuteAsync (与 ChatFlow 内部调用同路径) 验证取数闭环。

using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.ChatTools;
using PeakCan.Host.App.Services.J1939;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.ViewModels;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.Replay;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

public class TraceViewerGbt27930ClosedLoopTests
{
    private static readonly string FixtureDir = Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "Can");

    private const string RealAsc = "gbt27930-charge-hiccup-1.3s.asc";
    private const string RealDbc = "gbt27930_96_换电.dbc";

    // 虚拟帧 ID = J1939Id.Compose(priority, 0x000200, 0xF4, 0x56)。
    // 真实 RTS 帧 `1cec56f4x` 优先级 7 → 0x1C0256F4；DBC BRM `BO_ 2617399028`
    // = 0x9C0256F4 (bit31 惯例置位)，剥 bit31 后精确匹配。
    private const uint BrmVirtualId = 0x1C0256F4u;
    private const uint BrmDbcId = 0x9C0256F4u;

    [Fact]
    public async Task RealLog_RebuildJ1939Views_Produces_Complete_Brm_Virtual_Frames()
    {
        // Arrange — 真实 log + 真实 DBC + 真实 registry/reassembly
        var (vm, sourceId) = await BuildVmAsync();
        // review LOW-2: 顺带钉住 master 绑定（sourceId 解构不再闲置）
        vm.MasterSourceId.Should().Be(sourceId);

        // Act — 触发重组（与 SourceFlow 加载后同路径）
        vm.RebuildJ1939ViewsCommand.Execute(null);

        // Assert — 5 个完整 BRM 会话全部重组成 Complete 虚拟帧
        var complete = vm.ReassembledMessages.Where(m => m.Status == ReassemblyStatus.Complete).ToList();
        complete.Should().HaveCount(5, "fixture 含 5 个完整 RTS/CTS BRM 会话");
        complete.Should().OnlyContain(m => m.Message.Pgn == 0x000200, "BRM PGN");
        vm.DecodeFrames.Should().Contain(
            f => (f.Id & 0x7FFFFFFFu) == BrmVirtualId,
            "DecodeFrames 必须并入重组 BRM 虚拟帧");
    }

    [Fact]
    public async Task RealLog_GetSignalOverviewTool_Decodes_Multiframe_Brm_Signal()
    {
        // Arrange — 真实 log + 真实 DBC + 重组
        var (vm, sourceId) = await BuildVmAsync();
        vm.RebuildJ1939ViewsCommand.Execute(null);

        // 真实工具绑定 VM 上下文（与 ChatFlow.BuildChatTools 相同的 (IChatToolContext)this）
        IChatToolContext ctx = vm;
        var tool = new GetSignalOverviewTool(ctx, NullLogger<GetSignalOverviewTool>.Instance);

        // Act — 请求 BRM 的 VIN 信号；key 用 DBC 原始 ID (bit31 置位)
        var result = await tool.ExecuteAsync(
            $$"""{"signal_keys":["0x{{BrmDbcId:x8}}.VIN_Copy_17"]}""",
            CancellationToken.None);

        // Assert — 多帧信号从重组虚拟帧解码出 5 帧 (5 个 BRM 会话)，非 "no frames"
        var root = JsonNode.Parse(result)!.AsObject();
        var signals = root["signals"]!.AsArray();
        signals.Should().HaveCount(1);
        var sig = signals[0]!.AsObject();
        sig["error"].Should().BeNull("多帧信号必须从重组虚拟帧取到数据，而不是报 no frames");
        sig["total_frames"]!.GetValue<int>().Should().Be(5, "5 个 BRM 会话各产出一帧虚拟帧");

        // review LOW-1: VIN 已在 fixture 掩码为全 'A' (0x41)，5 会话同值——
        // 断言精确值 0x41 同时钉死字节偏移 (byte 40 = VIN_Copy_17，不能读到
        // 相邻的 byte 39/41) 与掩码生效，一箭双雕。
        var first = sig["statistics"]!["first"]!.GetValue<double>();
        first.Should().Be(0x41, "VIN_Copy_17 起始位 320 落在掩码后的 'A' 字节 (byte 40)");
    }

    [Fact]
    public async Task RealLog_SearchSignalTraceTool_Finds_Multiframe_Values_At_Timestamps()
    {
        // Arrange
        var (vm, _) = await BuildVmAsync();   // review LOW-2: sourceId 此处不需要
        vm.RebuildJ1939ViewsCommand.Execute(null);
        IChatToolContext ctx = vm;
        var tool = new SearchSignalTraceTool(ctx, NullLogger<SearchSignalTraceTool>.Instance);

        // Act — 对多帧信号做时间范围采样（应命中重组虚拟帧）
        var result = await tool.ExecuteAsync(
            $$"""{"signal_keys":["0x{{BrmDbcId:x8}}.VIN_Copy_17"],"t_start":0.0,"t_end":2.0,"max_points":10}""",
            CancellationToken.None);

        // Assert — 非空且含 5 个采样点
        var root = JsonNode.Parse(result)!.AsObject();
        root["error"].Should().BeNull("search_signal_trace 应能对多帧信号取到重组虚拟帧数据");
        var signals = root["signals"]?.AsArray();
        signals.Should().NotBeNull();
        signals![0]!["sample_count"]!.GetValue<int>()
            .Should().Be(5, "5 个 BRM 虚拟帧都在 0-2s 窗口内");
    }

    /// <summary>
    /// 加载真实 ASC + DBC，构造真实 registry + VM（含真实 J1939ReassemblyService）。
    /// 返回 VM 与 sourceId，测试随后触发 RebuildJ1939Views。
    /// </summary>
    private static async Task<(TraceViewerViewModel Vm, string SourceId)> BuildVmAsync()
    {
        var ascPath = Path.Combine(FixtureDir, RealAsc);
        var dbcPath = Path.Combine(FixtureDir, RealDbc);
        File.Exists(ascPath).Should().BeTrue($"fixture {ascPath} 必须存在");
        File.Exists(dbcPath).Should().BeTrue($"fixture {dbcPath} 必须存在");

        // 真实 DBC 解析
        var dbcText = await File.ReadAllTextAsync(dbcPath);
        var dbcResult = DbcParser.Parse(dbcText);
        dbcResult.IsSuccess.Should().BeTrue($"真实 DBC 必须解析成功: {dbcResult.Error?.Message}");
        var dbcService = new DbcService(NullLogger<DbcService>.Instance);
        dbcService.SetCurrentForTests(dbcResult.Value!);

        // 真实 ASC 加载（与 TraceViewerViewModelFixtureIntegrationTests 同路径）
        var registry = new TraceSessionRegistry(new TableauPalette(), NullLoggerFactory.Instance);
        var source = await registry.LoadAsync(ascPath);

        // 真实 VM：真实 session (非空集合避免 NRE) + 真实 registry + 真实重组服务
        var session = Substitute.For<ITraceSessionService>();
        session.WatchedSignals.Returns(new ObservableCollection<WatchedSignalRow>());
        session.SignalGroups.Returns(new ObservableCollection<WatchedSignalGroup>());
        var vm = new TraceViewerViewModel(
            session, registry, dbcService, NullLogger<TraceViewerViewModel>.Instance,
            new TraceSessionLibrary(
                Path.Combine(Path.GetTempPath(), $"tmtrace-gbt-{Guid.NewGuid():N}.tmtrace"),
                NullLogger<TraceSessionLibrary>.Instance),
            j1939Reassembly: new PeakCan.Host.App.Services.J1939.J1939ReassemblyService());
        vm.MasterSourceId = source.SourceId;
        return (vm, source.SourceId);
    }
}
