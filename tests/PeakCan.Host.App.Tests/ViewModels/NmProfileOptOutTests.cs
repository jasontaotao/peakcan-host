using System.Collections.ObjectModel;
using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.ChatTools;
using PeakCan.Host.App.Services.Nm;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// 用户主动 opt-out（选「未选择企标」）之后，**必须仍能选回来**。
/// <para>
/// <b>这是实测发现的回归</b>：首版用 <c>SearchResult.None</c> 表达 opt-out，
/// 而它的 <c>Candidates</c> 是空数组 → 选择器塌缩成只有「未选择」一项 →
/// 用户点一次「未选择」就永久失去选择能力，只能重启会话。
/// </para>
/// <para>
/// <b>为什么单元测试没抓住</b>：<see cref="NmProfileSelectorTests"/> 手工构造
/// <c>NmToolSnapshot.ProfileCandidates</c>，永远不经过 <c>NmFlow</c> 的搜索路径。
/// 这组测试走真实快照，钉住「opt-out 时候选列表非空」。
/// </para>
/// </summary>
public class NmProfileOptOutTests
{
    private const string DbcText = """
        VERSION ""

        BO_ 1280 NM_Message: 8 ECU_A
        """;

    private static readonly ReplayFrame[] Frames =
    [
        new(0.0, 0x500, 8, new byte[] { 0xF4, 0, 0, 0, 0, 0, 0, 0 }, FrameFlags.None),
        new(0.5, 0x500, 8, new byte[] { 0xF5, 0, 0, 0, 0, 0, 0, 0 }, FrameFlags.None),
        new(1.0, 0x500, 8, new byte[] { 0xF6, 0, 0, 0, 0, 0, 0, 0 }, FrameFlags.None),
    ];

    private const string BaseProfile = """
        standard:
          id: base-test
          version: "1.0"
        """;

    private const string OemProfile = """
        standard:
          id: oem-test
          version: "2.0"
          extends: base-test@1.0
        """;

    /// <summary>
    /// 建 VM，并把 profile 根目录指向一个临时目录（里面放 base + oem）。
    /// 返回的 <c>string</c> 是临时目录路径，调用方负责删除。
    /// </summary>
    private static TraceViewerViewModel NewVm(out string profileRoot)
    {
        profileRoot = Path.Combine(Path.GetTempPath(), $"nm-optout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(profileRoot, "base"));
        Directory.CreateDirectory(Path.Combine(profileRoot, "oem"));
        File.WriteAllText(
            Path.Combine(profileRoot, "base", "base-test@1.0.nmstd.yaml"), BaseProfile);
        File.WriteAllText(
            Path.Combine(profileRoot, "oem", "oem-test@2.0.nmstd.yaml"), OemProfile);

        var session = Substitute.For<ITraceSessionService>();
        session.WatchedSignals.Returns(new ObservableCollection<WatchedSignalRow>());
        session.SignalGroups.Returns(new ObservableCollection<WatchedSignalGroup>());
        var registry = Substitute.For<ITraceSessionRegistry>();
        var viewer = Substitute.For<ITraceViewerService>();
        viewer.LoadedFrames.Returns(Frames);
        registry.GetService("src").Returns(viewer);
        var dbcService = new DbcService(NullLogger<DbcService>.Instance);
        dbcService.SetCurrentForTests(DbcParser.Parse(DbcText).Value!);
        var libPath = Path.Combine(Path.GetTempPath(), $"nm-optout-lib-{Guid.NewGuid():N}.tmtrace");
        // AppData 层指向临时目录下的空子目录：既隔离真实的 %APPDATA%，
        // 又不让这一层命中任何 profile（否则会短路掉安装目录层）。
        var emptyAppData = Path.Combine(profileRoot, "appdata-empty");
        Directory.CreateDirectory(emptyAppData);

        var vm = new TraceViewerViewModel(session, registry, dbcService,
            NullLogger<TraceViewerViewModel>.Instance,
            new TraceSessionLibrary(libPath, NullLogger<TraceSessionLibrary>.Instance))
        {
            // 指向临时目录：不碰真实的 %APPDATA% 与安装目录。
            NmAppDataProfilesRoot = emptyAppData,
            NmInstallProfilesRoot = profileRoot,
        };
        return vm;
    }

    private static Task<NmToolSnapshot> SnapshotAsync(TraceViewerViewModel vm)
        => ((IChatToolContext)vm).GetNmSnapshotAsync(
            new NmToolQuery(SourceId: "src"), CancellationToken.None);

    /// <summary>
    /// <b>核心回归</b>：opt-out 之后候选列表必须仍然完整。
    /// <para>
    /// 早先这里是空数组，选择器只剩「未选择」一项，用户无法选回任何 profile。
    /// </para>
    /// </summary>
    [Fact]
    public async Task OptOut_StillListsAllCandidates()
    {
        var vm = NewVm(out var root);
        try
        {
            // 先跑一次：自动选中叶子（oem-test），候选两份。
            var before = await SnapshotAsync(vm);
            before.ProfileCandidates.Should().HaveCount(2);
            before.Report.StandardId.Should().Be("oem-test@2.0",
                "base 被 oem 继承，默认应选叶子");

            // 用户主动退回「未选择企标」。
            vm.NmProfileOptOut = true;

            var after = await SnapshotAsync(vm);

            after.Report.StandardId.Should().BeNull("opt-out 后不跑企标规则");
            after.ProfileCandidates.Should().HaveCount(2,
                "opt-out 只是不选中，不能把候选列表也清空——否则用户永久失去选择能力");
            after.ProfileCandidates.Select(c => c.Id)
                .Should().BeEquivalentTo(["base-test@1.0", "oem-test@2.0"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// <b>opt-out 是坏 profile 时用户的唯一逃生路径</b>：目录里有一份解析不了的
    /// <c>*.nmstd.yaml</c> 时，opt-out 分析必须照常跑完，不能整次失败。
    /// <para>
    /// 早先 opt-out 用 <c>SearchResult.None</c>，完全不碰文件 IO，正好是坏 profile 下的
    /// 逃生路径。改成 <c>EnumerateOnly</c> 后若不做容错，坏文件会让
    /// <c>LoadCandidate</c> 抛异常 → 整次分析失败 → <c>ClearNmReport</c> 还会把
    /// <c>NmProfileOptOut</c> 清掉 → 用户被永久困在「分析失败」，连「不用企标」都选不了。
    /// </para>
    /// </summary>
    [Fact]
    public async Task OptOut_WithBrokenProfileFile_StillCompletesAnalysis()
    {
        var vm = NewVm(out var root);
        try
        {
            // 放一份解析不了的文件（缺必填的 standard.id）。
            File.WriteAllText(
                Path.Combine(root, "oem", "broken@9.9.nmstd.yaml"),
                "standard:\n  version: \"9.9\"\n");

            vm.NmProfileOptOut = true;

            var snapshot = await SnapshotAsync(vm);

            snapshot.Report.StandardId.Should().BeNull("opt-out 不跑企标规则");
            // 坏文件不能让整次分析失败，也不能让用户失去 opt-out 状态。
            vm.NmProfileOptOut.Should().BeTrue("分析成功不该清掉用户的选择");
            // 坏文件也不能静默消失：必须能被用户看到。
            snapshot.ProfileCandidates.Should().Contain(c => c.Id == "oem-test@2.0",
                "好的候选仍要列出来");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// opt-out 时的文案不能说「未命中」，也不能谎称正在监听。
    /// <para>
    /// opt-out 走 <c>EnumerateOnly</c>，<c>Level</c> 是 null；不特判会落进
    /// 「未命中」分支，界面显示「共 2 份候选，请选择（未命中）」——用户刚主动选了
    /// 「未选择」，被告知搜索未命中，且「请选择」与当前选中项自相矛盾。
    /// </para>
    /// </summary>
    [Fact]
    public async Task OptOut_ShowsObservableHint_NotMissedOrWatching()
    {
        var vm = NewVm(out var root);
        try
        {
            var snapshot = await SnapshotAsync(vm);
            vm.ApplyNmReport(snapshot);

            vm.NmProfileOptOut = true;
            var optedOut = await SnapshotAsync(vm);
            vm.ApplyNmReport(optedOut);

            vm.NmProfileCandidatesSummary.Should().NotContain("未命中",
                "opt-out 是用户主动选择，不是搜索失败");
            vm.NmProfileCandidatesSummary.Should().Contain("仅观测",
                "要说明当前状态是只观测");
            vm.NmProfileWatcherStatus.Should().BeEmpty(
                "opt-out 时没有生效的 profile，不能谎称正在监听");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// <b>watcher 必须挂在「实际生效」的那份 profile 所在目录</b>，不能取候选 [0]。
    /// <para>
    /// 安装目录同时有 base 与 oem 时，候选按路径排序 base 在前，而生效的是 oem。
    /// 挂在 base 目录上，用户改 oem 的 yaml 不会触发任何重算，
    /// 而状态文案写着「仅监听所选 profile 所在目录」——与事实不符。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Watcher_WatchesDirectoryOfEffectiveProfile_NotFirstCandidate()
    {
        var vm = NewVm(out var root);
        var watched = new List<string>();
        vm.NmProfileWatcherFactory = dir =>
        {
            watched.Add(dir);
            return Substitute.For<INmProfileWatcher>();
        };
        try
        {
            var snapshot = await SnapshotAsync(vm);

            // 生效的是叶子 oem-test@2.0，而候选 [0] 是 base。
            snapshot.Report.StandardId.Should().Be("oem-test@2.0");

            // watcher 在 ApplyNmReport 里启动（不是 GetNmSnapshotAsync 的职责）——
            // 模拟 UI 拿到快照后的那一步。
            vm.ApplyNmReport(snapshot);

            watched.Should().NotBeEmpty("选中了 profile 就必须监听它的目录");
            watched.Should().OnlyContain(d =>
                    string.Equals(
                        Path.GetFileName(d.TrimEnd(Path.DirectorySeparatorChar)),
                        "oem", StringComparison.OrdinalIgnoreCase),
                "必须监听生效的 oem 目录，不是候选 [0] 的 base 目录");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// opt-out 后重新选中某份 profile，必须真的切回去。
    /// 这条走的是用户的完整操作序列：选「未选择」→ 再选 geely。
    /// </summary>
    [Fact]
    public async Task OptOutThenSelectAgain_RestoresProfile()
    {
        var vm = NewVm(out var root);
        try
        {
            await SnapshotAsync(vm);
            vm.NmProfileOptOut = true;
            await SnapshotAsync(vm);

            // 用户从下拉里选回 oem。
            vm.NmProfileOptOut = false;
            var candidates = (await SnapshotAsync(vm)).ProfileCandidates;
            vm.NmExplicitProfilePath = candidates.Single(c => c.StandardId == "oem-test").Path;

            var restored = await SnapshotAsync(vm);

            restored.Report.StandardId.Should().Be("oem-test@2.0");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
