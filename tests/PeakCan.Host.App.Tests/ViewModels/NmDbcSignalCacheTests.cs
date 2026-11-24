using System.Collections.ObjectModel;
using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.ChatTools;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// DBC 信号定义参与 NM 报告缓存 key（spec §10.1 的补充）。
/// <para>
/// canonical profile hash 只覆盖报文/节点/布局/时序；但分析器还直接消费 DBC 的
/// 信号定义（<c>signal_change</c> 事件、NMSts 编码交叉校验、<c>SignalDecode</c> 能力）。
/// 只改信号时若 key 不变，会命中旧报告。
/// </para>
/// </summary>
public class NmDbcSignalCacheTests
{
    private const string DbcWithCounter = """
        VERSION ""

        BO_ 1280 NM_Message: 8 ECU_A
         SG_ Counter : 0|8@1+ (1,0) [0|255] "" ECU_B
        """;

    /// <summary>同一报文，但信号起始位不同——解码结果会变。</summary>
    private const string DbcWithShiftedCounter = """
        VERSION ""

        BO_ 1280 NM_Message: 8 ECU_A
         SG_ Counter : 1|8@1+ (1,0) [0|255] "" ECU_B
        """;

    private static readonly ReplayFrame[] Frames =
    [
        new(0.0, 0x500, 8, new byte[] { 0xF4, 0, 0, 0, 0, 0, 0, 0 }, FrameFlags.None),
        new(0.5, 0x500, 8, new byte[] { 0xF5, 0, 0, 0, 0, 0, 0, 0 }, FrameFlags.None),
        new(1.0, 0x500, 8, new byte[] { 0xF6, 0, 0, 0, 0, 0, 0, 0 }, FrameFlags.None),
    ];

    /// <summary>
    /// 建 VM，并把注入的 <paramref name="dbcService"/> 交回给测试——
    /// 「同一个 VM 上换 DBC」必须通过它改 Current，新建 VM 无法验证 per-VM 缓存。
    /// </summary>
    private static TraceViewerViewModel NewVm(DbcDocument dbc, out DbcService dbcService)
    {
        var session = Substitute.For<ITraceSessionService>();
        session.WatchedSignals.Returns(new ObservableCollection<WatchedSignalRow>());
        session.SignalGroups.Returns(new ObservableCollection<WatchedSignalGroup>());
        var registry = Substitute.For<ITraceSessionRegistry>();
        var viewer = Substitute.For<ITraceViewerService>();
        viewer.LoadedFrames.Returns(Frames);
        registry.GetService("src").Returns(viewer);
        dbcService = new DbcService(NullLogger<DbcService>.Instance);
        dbcService.SetCurrentForTests(dbc);
        var libPath = Path.Combine(Path.GetTempPath(), $"nm-dbcsig-{Guid.NewGuid():N}.tmtrace");
        return new TraceViewerViewModel(session, registry, dbcService,
            NullLogger<TraceViewerViewModel>.Instance,
            new TraceSessionLibrary(libPath, NullLogger<TraceSessionLibrary>.Instance));
    }

    private static DbcDocument Parse(string text) => DbcParser.Parse(text).Value!;

    [Fact]
    public async Task SameDbcTwice_HitsCache()
    {
        // 对照组：DBC 未变时必须命中缓存，否则每次分析都白跑。
        var vm = NewVm(Parse(DbcWithCounter), out _);
        var first = await ((IChatToolContext)vm).GetNmSnapshotAsync(
            new NmToolQuery(SourceId: "src"), CancellationToken.None);
        var second = await ((IChatToolContext)vm).GetNmSnapshotAsync(
            new NmToolQuery(SourceId: "src"), CancellationToken.None);

        // 同一份报告实例 = 命中缓存（新算会产新对象）。
        second.Report.Should().BeSameAs(first.Report);
    }

    [Fact]
    public async Task ChangedDbcSignal_DoesNotHitCache()
    {
        // 核心场景：sourceId、帧数、窗口、NM 属性全部相同，只有 DBC 信号位布局不同。
        // 若 key 不含 DBC 信号语义，第二次会复用第一份报告。
        var vm = NewVm(Parse(DbcWithCounter), out var dbcService);
        var warm = await ((IChatToolContext)vm).GetNmSnapshotAsync(
            new NmToolQuery(SourceId: "src"), CancellationToken.None);
        warm.Report.Should().NotBeNull();

        // 同一 VM 上换 DBC（模拟 DbcService 重新加载）。
        dbcService.SetCurrentForTests(Parse(DbcWithShiftedCounter));
        var second = await ((IChatToolContext)vm).GetNmSnapshotAsync(
            new NmToolQuery(SourceId: "src"), CancellationToken.None);

        second.Report.Should().NotBeSameAs(warm.Report,
            "DBC 信号位布局变了，报告必须重算（signal_change 事件与 NMSts 校验都依赖它）");
    }
}
