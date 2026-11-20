using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.ChatTools;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.ViewModels;
using PeakCan.HIL.Core.Dbc;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.ChatTools;

/// <summary>
/// spec §8.1 的窗口钻取在真实 NmFlow 缓存上的证据：同一 (source, channel,
/// start, end, windowRef) 必须命中同一份 report（不重新扫帧），不同窗口必须重算。
/// 工具层「是否把窗口参数原样传下去」由 FakeChatToolContext.NmQueries 覆盖。
/// </summary>
public class GetNmToolWindowCacheTests
{
    private static readonly string FixtureDir = Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "Can");

    [Fact]
    public async Task SameWindowQuery_ReusesCachedReport_DifferentWindowRescans()
    {
        var vm = await BuildVmAsync();
        IChatToolContext ctx = vm;
        // fixture 跨两个通道：显式 channel 才能进入分析路径。
        var query = new NmToolQuery(Channel: 1, TStart: 0.1, TEnd: 0.5, WindowRef: "w1");

        var first = await ctx.GetNmSnapshotAsync(query, CancellationToken.None);
        var second = await ctx.GetNmSnapshotAsync(query, CancellationToken.None);

        ReferenceEquals(first.Report, second.Report).Should().BeTrue(
            "同窗口必须命中 NmFlow 缓存，钻取不重新扫帧");
        second.Report.Window.Start.Should().Be(0.1);
        second.Report.Window.End.Should().Be(0.5);
        second.Report.Window.WindowRef.Should().Be("w1");

        var other = await ctx.GetNmSnapshotAsync(query with { TEnd = 0.6 }, CancellationToken.None);

        ReferenceEquals(second.Report, other.Report).Should().BeFalse("不同窗口必须重新分析");
        other.Report.Window.End.Should().Be(0.6);
    }

    [Fact]
    public async Task SingleLoadedSource_WithoutSourceId_DefaultsToMaster()
    {
        var vm = await BuildVmAsync();
        IChatToolContext ctx = vm;
        var tool = new GetNmOverviewTool(ctx, NullLogger<GetNmOverviewTool>.Instance);

        // fixture 多通道：显式 channel 但省略 source_id；单源时必须仍取 master。
        var json = JsonNode.Parse(await tool.ExecuteAsync(
            """{"channel":1}""", CancellationToken.None))!.AsObject();

        json["error"].Should().BeNull();
        json["source_id"]!.GetValue<string>().Should().Be(vm.MasterSourceId);
    }

    private static async Task<TraceViewerViewModel> BuildVmAsync()
    {
        var ascPath = Path.Combine(FixtureDir, "gbt27930-charge-hiccup-1.3s.asc");
        var dbcPath = Path.Combine(FixtureDir, "gbt27930_96_换电.dbc");
        File.Exists(ascPath).Should().BeTrue($"fixture {ascPath} 必须存在");
        File.Exists(dbcPath).Should().BeTrue($"fixture {dbcPath} 必须存在");

        var parsed = DbcParser.Parse(await File.ReadAllTextAsync(dbcPath));
        parsed.IsSuccess.Should().BeTrue($"fixture DBC 必须可解析: {parsed.Error?.Message}");
        var dbcService = new DbcService(NullLogger<DbcService>.Instance);
        dbcService.SetCurrentForTests(parsed.Value!);

        var registry = new TraceSessionRegistry(new TableauPalette(), NullLoggerFactory.Instance);
        var source = await registry.LoadAsync(ascPath);

        // 真实 session（非空集合避免 NRE）+ 真实 registry。
        var session = Substitute.For<ITraceSessionService>();
        session.WatchedSignals.Returns(new ObservableCollection<WatchedSignalRow>());
        session.SignalGroups.Returns(new ObservableCollection<WatchedSignalGroup>());
        var vm = new TraceViewerViewModel(
            session, registry, dbcService, NullLogger<TraceViewerViewModel>.Instance,
            new TraceSessionLibrary(
                Path.Combine(Path.GetTempPath(), $"tmtrace-nm-cache-{Guid.NewGuid():N}.tmtrace"),
                NullLogger<TraceSessionLibrary>.Instance));
        vm.MasterSourceId = source.SourceId;
        return vm;
    }
}
