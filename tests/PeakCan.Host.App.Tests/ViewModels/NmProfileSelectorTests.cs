using System.Collections.ObjectModel;
using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.ChatTools;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// 企标选择器与标定面板的 VM 行为（spec §10.1 / §10.2）。
/// <para>
/// 验收要求「切 profile 必重算」：这里钉的是**切换动作会写进显式选择并触发重分析**。
/// 重算本身由缓存 key 的 canonical profile hash 保证（Core 侧另有测试）。
/// </para>
/// </summary>
public class NmProfileSelectorTests
{
    private static readonly string[] Reasons = ["MsgType=CANNM"];

    private static NmReport Report(
        string? standardId = null,
        IReadOnlyList<NmFinding>? findings = null,
        IReadOnlyList<NmObservation>? observations = null,
        IReadOnlyList<NmUnmodeledRule>? unmodeled = null,
        IReadOnlyList<NmDataQualityCheck>? checks = null)
        => new(
            new NmWindow(0, 10, "full"), "src", null,
            Array.Empty<NmEpisode>(), Array.Empty<NmMessageReport>(), Array.Empty<NmNodeReport>(),
            Array.Empty<NmEvent>(),
            findings ?? Array.Empty<NmFinding>(), Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<NmCapabilityState>(), Array.Empty<NmInputMissing>(),
            Array.Empty<NmTruncation>(),
            StandardId: standardId,
            Observations: observations,
            UnmodeledRules: unmodeled,
            DataQualityChecks: checks);

    private static NmProfile Profile(IReadOnlyList<NmRuleDef>? rules = null)
        => new(
            NmProfileSource.Dbc,
            [new NmMessageDef("0x500", false, "NM", "A", 8, true, Reasons, 500, 2000)],
            new NmTimings(CycleMs: 500, TimeoutMs: 2000),
            Array.Empty<NmNodeDef>(), Array.Empty<NmConfigConflict>(),
            Array.Empty<string>(), new NmProvenance(),
            StandardRules: rules);

    private static NmRuleDef Rule(string id)
        => new(id, "req " + id, "normal",
            new NmRuleWhen("cycle_tolerance", CycleMs: 500),
            new NmRuleExpect("within_pct", Pct: 0.1),
            "NM_CYCLE_DEVIATION")
        {
            Clauses = ["§5.5"],
        };

    private static NmToolSnapshot Snapshot(
        NmReport report,
        NmProfile profile,
        IReadOnlyList<NmStandardProfileLocator.Candidate>? candidates = null,
        NmProfileSearchLevel? level = null)
        => new("src", null, null, null, profile, report, Array.Empty<ReplayFrame>())
        {
            ProfileCandidates = candidates ?? Array.Empty<NmStandardProfileLocator.Candidate>(),
            ProfileLevel = level,
        };

    private static TraceViewerViewModel NewVm()
    {
        var session = Substitute.For<ITraceSessionService>();
        session.WatchedSignals.Returns(new ObservableCollection<WatchedSignalRow>());
        session.SignalGroups.Returns(new ObservableCollection<WatchedSignalGroup>());
        var registry = Substitute.For<ITraceSessionRegistry>();
        registry.Sources.Returns(new List<TraceSource>());
        var dbcService = Substitute.For<DbcService>(NullLogger<DbcService>.Instance);
        var libPath = Path.Combine(Path.GetTempPath(), $"nm-sel-{Guid.NewGuid():N}.tmtrace");
        return new TraceViewerViewModel(session, registry, dbcService,
            NullLogger<TraceViewerViewModel>.Instance,
            new TraceSessionLibrary(libPath, NullLogger<TraceSessionLibrary>.Instance));
    }

    // —— 选择器 ——

    [Fact]
    public void ApplyNmReport_NoProfile_SelectorShowsNoneAndHint()
    {
        var vm = NewVm();
        vm.ApplyNmReport(Snapshot(Report(), Profile()));

        vm.NmProfileChoices.Should().ContainSingle()
            .Which.IsNone.Should().BeTrue("「未选择企标」必须是可选项");
        vm.NmSelectedProfile!.IsNone.Should().BeTrue();
        vm.NmProfileSelectorHint.Should().Contain("仅做观测");
    }

    [Fact]
    public void ApplyNmReport_WithCandidates_ListsThemAndKeepsNoneFirst()
    {
        var candidates = new[]
        {
            new NmStandardProfileLocator.Candidate(
                @"C:\p\geely-cv@2.0.nmstd.yaml", "geely-cv", "2.0", NmProfileSearchLevel.AppData),
            new NmStandardProfileLocator.Candidate(
                @"C:\p\autosar@4.1.3.nmstd.yaml", "autosar-cannm", "4.1.3", NmProfileSearchLevel.AppData),
        };

        var vm = NewVm();
        vm.ApplyNmReport(Snapshot(Report(), Profile(), candidates, NmProfileSearchLevel.AppData));

        vm.NmProfileChoices.Should().HaveCount(3, "「未选择」+ 两份候选");
        vm.NmProfileChoices[0].IsNone.Should().BeTrue();
        vm.NmProfileChoices.Select(c => c.StandardId).Should().Contain("geely-cv");
        vm.NmProfileChoices.Select(c => c.StandardId).Should().Contain("autosar-cannm");
        vm.NmProfileCandidatesSummary.Should().Contain("2 份候选");
    }

    [Fact]
    public void ApplyNmReport_SelectedStandardId_MatchesChoice()
    {
        var candidates = new[]
        {
            new NmStandardProfileLocator.Candidate(
                @"C:\p\geely-cv@2.0.nmstd.yaml", "geely-cv", "2.0", NmProfileSearchLevel.DbcDirectory),
        };

        var vm = NewVm();
        vm.ApplyNmReport(Snapshot(
            Report(standardId: "geely-cv@2.0"), Profile(), candidates,
            NmProfileSearchLevel.DbcDirectory));

        vm.NmSelectedProfile!.StandardId.Should().Be("geely-cv");
        vm.NmProfileSelectorHint.Should().BeEmpty("已选中企标时不该再显示「未选择」提示");
    }

    [Fact]
    public void ApplyNmReport_SingleCandidate_ShowsLevel()
    {
        var candidates = new[]
        {
            new NmStandardProfileLocator.Candidate(
                @"C:\p\a@1.nmstd.yaml", "a", "1", NmProfileSearchLevel.InstallDirectory),
        };

        var vm = NewVm();
        vm.ApplyNmReport(Snapshot(Report(), Profile(), candidates,
            NmProfileSearchLevel.InstallDirectory));

        vm.NmProfileChoices[1].Display.Should().Contain("安装目录",
            "用户必须能看出这份企标是从哪一层找到的");
        vm.NmProfileCandidatesSummary.Should().Contain("安装目录");
    }

    /// <summary>
    /// 造一个「已分析完、候选列表就绪」的 VM，并抑制真实重分析
    /// （测试替身没有 trace 源，真跑会抛错并清空面板）。
    /// </summary>
    private static (TraceViewerViewModel Vm, List<int> RecomputeCount) NewVmWithSelection()
    {
        var vm = NewVm();
        var count = new List<int>();
        vm.NmProfileChangeRequested = () => count.Add(1);
        vm.ApplyNmReport(Snapshot(
            Report(standardId: "a@1"), Profile(),
            [
                new NmStandardProfileLocator.Candidate(
                    @"C:\p\a@1.nmstd.yaml", "a", "1", NmProfileSearchLevel.DbcDirectory),
            ],
            NmProfileSearchLevel.DbcDirectory));
        return (vm, count);
    }

    [Fact]
    public void SelectingProfile_WritesExplicitPathAndTriggersRecompute()
    {
        // 切 profile 必重算：显式选择写进 NmExplicitProfilePath，下一次查询的
        // Search 会优先用它，canonical profile hash 随之变化 → 缓存不命中。
        var (vm, recomputeCount) = NewVmWithSelection();
        // 先退回「未选择」再选具体 profile，确保触发一次真实变更。
        vm.NmSelectedProfile = vm.NmProfileChoices[0];
        recomputeCount.Clear();

        // 模拟用户在下拉里选中第二项（第一项是「未选择」）。
        vm.NmSelectedProfile = vm.NmProfileChoices[1];

        vm.NmExplicitProfilePath.Should().Be(@"C:\p\a@1.nmstd.yaml");
        recomputeCount.Should().ContainSingle("切换企标必须触发一次重算（spec §10.1）");
    }

    [Fact]
    public void SelectingNone_ClearsExplicitPath()
    {
        // 初始状态已按 StandardId 选中 a@1，但 NmExplicitProfilePath 仍为 null
        // （自动选中 ≠ 用户显式选择）。这里验证的是「先显式选、再退回未选择」。
        var (vm, _) = NewVmWithSelection();
        vm.NmSelectedProfile = vm.NmProfileChoices[0];
        vm.NmSelectedProfile = vm.NmProfileChoices[1];
        vm.NmExplicitProfilePath.Should().Be(@"C:\p\a@1.nmstd.yaml");

        vm.NmSelectedProfile = vm.NmProfileChoices[0];

        vm.NmExplicitProfilePath.Should().BeNull("退回「未选择」必须清掉显式选择");
    }

    /// <summary>
    /// <b>关键场景</b>：自动发现单份 profile（最常见）时，用户选「未选择」必须真的退回。
    /// <para>
    /// 首版把「用户不要企标」编码成 <c>ExplicitPath = null</c>，与「用户没表态」不可区分：
    /// 选「未选择」时两侧都是 null → 判定为「没变化」→ 不重算，
    /// 界面显示「未选择」但报告仍是那份 profile 的结果。
    /// </para>
    /// </summary>
    [Fact]
    public void SelectingNone_AfterAutoSelect_TriggersRecomputeAndOptsOut()
    {
        var (vm, recomputeCount) = NewVmWithSelection();
        // 初始状态：按 StandardId 自动选中了 a@1，但用户没表态。
        vm.NmSelectedProfile!.IsNone.Should().BeFalse();
        vm.NmProfileOptOut.Should().BeFalse();

        vm.NmSelectedProfile = vm.NmProfileChoices[0];

        vm.NmProfileOptOut.Should().BeTrue("用户主动选择「未选择」必须记成 opt-out");
        vm.NmExplicitProfilePath.Should().BeNull();
        recomputeCount.Should().ContainSingle("退回「未选择」必须重算，否则报告仍是旧 profile 的");
    }

    [Fact]
    public void SelectingNone_WhileAlreadyOptedOut_DoesNotRecomputeAgain()
    {
        // 幂等：已经处于 opt-out 时再选一次「未选择」不该重复重算。
        var (vm, recomputeCount) = NewVmWithSelection();
        vm.NmSelectedProfile = vm.NmProfileChoices[0];
        recomputeCount.Clear();

        vm.NmSelectedProfile = vm.NmProfileChoices[0];

        recomputeCount.Should().BeEmpty();
    }

    [Fact]
    public void SelectingProfile_AfterOptOut_ClearsOptOut()
    {
        var (vm, _) = NewVmWithSelection();
        vm.NmSelectedProfile = vm.NmProfileChoices[0];
        vm.NmProfileOptOut.Should().BeTrue();

        vm.NmSelectedProfile = vm.NmProfileChoices[1];

        vm.NmProfileOptOut.Should().BeFalse("重新选中 profile 必须清掉 opt-out");
        vm.NmExplicitProfilePath.Should().Be(@"C:\p\a@1.nmstd.yaml");
    }

    [Fact]
    public void SelectingSameProfileAgain_DoesNotRecompute()
    {
        // 同一个 profile 再选一次是 no-op：重复重算会白跑一次分析。
        var (vm, recomputeCount) = NewVmWithSelection();
        vm.NmSelectedProfile = vm.NmProfileChoices[1];
        recomputeCount.Clear();

        vm.NmSelectedProfile = vm.NmProfileChoices[1];

        recomputeCount.Should().BeEmpty();
    }

    [Fact]
    public void ApplyNmReport_RebuildingChoices_DoesNotClobberExplicitSelection()
    {
        // 刷新候选列表时 SelectedItem 会短暂变 null；若不做抑制，
        // 每次分析都会把用户的显式选择清掉。
        var candidates = new[]
        {
            new NmStandardProfileLocator.Candidate(
                @"C:\p\a@1.nmstd.yaml", "a", "1", NmProfileSearchLevel.DbcDirectory),
        };

        var vm = NewVm();
        vm.NmExplicitProfilePath = @"C:\p\a@1.nmstd.yaml";
        vm.ApplyNmReport(Snapshot(Report(standardId: "a@1"), Profile(), candidates,
            NmProfileSearchLevel.DbcDirectory));

        vm.NmExplicitProfilePath.Should().Be(@"C:\p\a@1.nmstd.yaml",
            "重建列表不得清掉用户已做的显式选择");
    }

    // —— 标定面板 ——

    [Fact]
    public void ApplyNmReport_WithRules_PopulatesCalibrationRows()
    {
        var vm = NewVm();
        vm.ApplyNmReport(Snapshot(Report(), Profile(rules: [Rule("NM_05"), Rule("NM_06")])));

        vm.NmCalibrationRows.Should().HaveCount(2);
        vm.NmHasCalibration.Should().BeTrue();
    }

    [Fact]
    public void ApplyNmReport_NoRulesAndNoChecks_HidesCalibrationPanel()
    {
        var vm = NewVm();
        vm.ApplyNmReport(Snapshot(Report(), Profile()));

        vm.NmCalibrationRows.Should().BeEmpty();
        vm.NmHasCalibration.Should().BeFalse("没有企标规则也没有体检结果时整块隐藏");
    }

    [Fact]
    public void ApplyNmReport_WithDataQuality_PopulatesChecks()
    {
        var report = Report(checks:
        [
            new NmDataQualityCheck("unknown_id", NmDataQualityStatus.Passed,
                "全部已知", Array.Empty<NmTimeRange>(), Array.Empty<string>()),
        ]);

        var vm = NewVm();
        vm.ApplyNmReport(Snapshot(report, Profile()));

        vm.NmDataQualityChecks.Should().ContainSingle();
        vm.NmHasCalibration.Should().BeTrue();
    }

    [Fact]
    public void ApplyNmReport_WithUnmodeled_PopulatesClauses()
    {
        var report = Report(unmodeled:
        [
            new NmUnmodeledRule("§5.5", "不可观测") { RuleId = "NM_17", ReasonCode = "unobservable" },
        ]);

        var vm = NewVm();
        vm.ApplyNmReport(Snapshot(report, Profile()));

        vm.NmUnmodeledClauses.Should().ContainSingle()
            .Which.RuleId.Should().Be("NM_17");
    }

    [Fact]
    public void ApplyNmReport_ClearsPreviousCalibration()
    {
        // 换 trace 后旧报告的四块表不能残留——它们属于上一份数据。
        var vm = NewVm();
        vm.ApplyNmReport(Snapshot(Report(), Profile(rules: [Rule("NM_05")])));
        vm.NmCalibrationRows.Should().NotBeEmpty();

        vm.ApplyNmReport(Snapshot(Report(), Profile()));

        vm.NmCalibrationRows.Should().BeEmpty();
    }

    [Fact]
    public void ClearNmReport_ResetsCalibrationAndSelector()
    {
        var vm = NewVm();
        vm.ApplyNmReport(Snapshot(Report(), Profile(rules: [Rule("NM_05")])));

        vm.ClearNmReportPublic();

        vm.NmCalibrationRows.Should().BeEmpty();
        vm.NmHasCalibration.Should().BeFalse();
        vm.NmProfileChoices.Should().BeEmpty();
        vm.NmSelectedProfile.Should().BeNull();
    }
}
