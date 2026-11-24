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
        IReadOnlyList<NmDataQualityCheck>? checks = null,
        IReadOnlyList<NmRuleEvaluation>? evaluations = null)
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
            DataQualityChecks: checks)
        {
            RuleEvaluations = evaluations ?? Array.Empty<NmRuleEvaluation>(),
        };

    private static NmProfile Profile(IReadOnlyList<NmRuleDef>? rules = null)
        => new(
            NmProfileSource.Dbc,
            [new NmMessageDef("0x500", false, "NM", "A", 8, true, Reasons, 500, 2000)],
            new NmTimings(CycleMs: 500, TimeoutMs: 2000),
            Array.Empty<NmNodeDef>(), Array.Empty<NmConfigConflict>(),
            Array.Empty<string>(), new NmProvenance(),
            StandardRules: rules);

    /// <summary>带报文名与事件的报告，用于验证事件表「对象」列。</summary>
    private static NmReport ReportWithEvent(string? messageName, NmEvent evt)
        => new(
            new NmWindow(0, 10, "full"), "src", null,
            Array.Empty<NmEpisode>(),
            [
                new NmMessageReport("0x18FFFA27", true, messageName, "ECU_A", 3, 0, 10,
                    new NmCycleStats(null, null, null, null, null, 0, 2),
                    Array.Empty<NmSegment>()),
            ],
            Array.Empty<NmNodeReport>(),
            [evt],
            Array.Empty<NmFinding>(), Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<NmCapabilityState>(), Array.Empty<NmInputMissing>(),
            Array.Empty<NmTruncation>());

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

    // —— 事件表「对象」列 ——

    /// <summary>
    /// <b>实测发现的问题</b>：事件表「对象」列原用 <c>Node ?? CanIdHex</c>，
    /// 于是快发事件显示发送节点（<c>ECU_A</c>）、字节变化显示报文 ID
    /// （<c>0x18FFFA27</c>）——同一列里两种东西，用户没法横向比较。
    /// </summary>
    [Fact]
    public void ApplyNmReport_EventRow_ShowsMessageNameForMessageEvents()
    {
        var vm = NewVm();
        vm.ApplyNmReport(Snapshot(
            ReportWithEvent("NM_Message",
                new NmEvent(1.0, "burst_observed", "ECU_A", "0x18FFFA27",
                    "3 帧短间隔", 0, "measured", IsExtended: true)),
            Profile()));

        var row = vm.NmEventRows.Should().ContainSingle().Subject;
        row.SubjectLabel.Should().Be("NM_Message (0x18FFFA27)",
            "报文类事件要显示「名字 (ID)」，不能只给节点名");
        // 原始 Subject 保留：筛选与导出仍按它匹配，行为不变。
        row.Subject.Should().Be("ECU_A");
    }

    /// <summary>DBC 没给报文名时退化成纯 CAN ID，不能出现空括号。</summary>
    [Fact]
    public void ApplyNmReport_EventRow_WithoutMessageName_FallsBackToCanId()
    {
        var vm = NewVm();
        vm.ApplyNmReport(Snapshot(
            ReportWithEvent(null,
                new NmEvent(1.0, "byte_change", null, "0x18FFFA27",
                    "byte0: 0x00 → 0x01", 0, "measured", IsExtended: true)),
            Profile()));

        vm.NmEventRows.Single().SubjectLabel.Should().Be("0x18FFFA27");
    }

    /// <summary>
    /// 全网静默这类事件没有 CAN ID，只有节点语境：此时保留原来的节点/空串，
    /// 不能因为「没有报文定义」就显示成空白或崩溃。
    /// </summary>
    [Fact]
    public void ApplyNmReport_NetworkLevelEvent_KeepsNodeAsSubject()
    {
        var vm = NewVm();
        vm.ApplyNmReport(Snapshot(
            ReportWithEvent("NM_Message",
                new NmEvent(1.0, "all_nm_silent", null, null,
                    "1.50s", 0, "inferred")),
            Profile()));

        var row = vm.NmEventRows.Should().ContainSingle().Subject;
        row.SubjectLabel.Should().BeEmpty("没有 CAN ID 也没有节点时保持原样");
        row.Subject.Should().BeEmpty();
    }

    /// <summary>
    /// <b>无名报文 + 快发事件</b>：这是本次最核心的行为变化。
    /// <para>
    /// 改前「对象」列显示发送节点 <c>ECU_A</c>（快发事件的 <c>Node</c> 非空），
    /// 改后即使没有报文名也必须以 CAN ID 为锚——否则列语义又会随事件类型漂移。
    /// </para>
    /// </summary>
    [Fact]
    public void ApplyNmReport_UnnamedMessage_BurstEvent_AnchorsOnCanIdNotNode()
    {
        var vm = NewVm();
        vm.ApplyNmReport(Snapshot(
            ReportWithEvent(null,
                new NmEvent(1.0, "burst_observed", "ECU_A", "0x18FFFA27",
                    "3 帧短间隔", 0, "measured", IsExtended: true)),
            Profile()));

        var row = vm.NmEventRows.Should().ContainSingle().Subject;
        row.SubjectLabel.Should().Be("0x18FFFA27",
            "没有报文名时以 CAN ID 为锚，不能退回发送节点");
        row.Subject.Should().Be("ECU_A", "原始 Subject 保留给导出");
    }

    /// <summary>
    /// 事件带 CAN ID 但报告里没有对应报文定义（如手搓报告 / 部分分析）：
    /// 必须退回 CAN ID，不能退回节点名——否则同一列又变成两种语义。
    /// </summary>
    [Fact]
    public void ApplyNmReport_EventWithUnknownCanId_FallsBackToCanId()
    {
        var vm = NewVm();
        vm.ApplyNmReport(Snapshot(
            ReportWithEvent("NM_Message",
                new NmEvent(1.0, "burst_observed", "ECU_A", "0x999",
                    "3 帧短间隔", 0, "measured")),
            Profile()));

        vm.NmEventRows.Single().SubjectLabel.Should().Be("0x999");
    }

    /// <summary>
    /// <b>本次引入的回归</b>：「对象」列显示的是 <see cref="NmEventRowVm.SubjectLabel"/>，
    /// 而筛选原先只匹配 <see cref="NmEventRowVm.Subject"/>——用户输入界面上看得见的
    /// 报文名反而搜不到该行。
    /// </summary>
    [Fact]
    public void ApplyNmReport_KeywordFilter_MatchesDisplayedSubjectLabel()
    {
        var vm = NewVm();
        vm.ApplyNmReport(Snapshot(
            ReportWithEvent("NM_Message",
                new NmEvent(1.0, "burst_observed", "ECU_A", "0x18FFFA27",
                    "3 帧短间隔", 0, "measured", IsExtended: true)),
            Profile()));

        // 报文名只出现在 SubjectLabel 里，Subject 是节点名。
        vm.NmEventKeyword = "NM_Message";
        vm.NmEventRows.Should().ContainSingle("输入界面显示的名字必须能搜到");

        // CAN ID 同理。
        vm.NmEventKeyword = "0x18FFFA27";
        vm.NmEventRows.Should().ContainSingle("输入界面显示的 ID 必须能搜到");

        // 原始 Subject 仍可搜（行为不回退）。
        vm.NmEventKeyword = "ECU_A";
        vm.NmEventRows.Should().ContainSingle();
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

    /// <summary>
    /// 规则表顶部的一句话汇总必须被填充——平铺 21 行时用户得自己数哪几条挂了。
    /// <para>
    /// 只断言绑定存在是不够的：把 <c>ApplyNmCalibration</c> 里那行赋值删掉，
    /// 面板顶部会永久空白，而布局测试照样全绿。
    /// </para>
    /// </summary>
    [Fact]
    public void ApplyNmReport_PopulatesCalibrationSummary()
    {
        var vm = NewVm();
        vm.ApplyNmReport(Snapshot(Report(), Profile(rules: [Rule("NM_05"), Rule("NM_06")])));

        vm.NmCalibrationSummary.Should().NotBeNullOrWhiteSpace();
        vm.NmCalibrationSummary.Should().Contain("2 条");
    }

    /// <summary>
    /// 13 条同质规则（预留位检查）必须折叠成一组，否则占掉半屏。
    /// </summary>
    [Fact]
    public void ApplyNmReport_CollapsesHomogeneousRules()
    {
        var vm = NewVm();
        var rules = Enumerable.Range(1, 6)
            .Select(i => new NmRuleDef($"NM_RESERVED_B1_{i}",
                $"CBV byte1.bit{i} 为预留位，必须填 0", "normal",
                new NmRuleWhen("bit_rule", Byte: 1, Bit: i),
                new NmRuleExpect("is_zero"), "NM_RESERVED_BIT_SET")
            {
                Clauses = ["§5.4.3"],
            })
            .ToArray();

        vm.ApplyNmReport(Snapshot(Report(), Profile(rules: rules)));

        // 平铺行仍保留（导出/统计用），展示层折叠成一组。
        vm.NmCalibrationRows.Should().HaveCount(6);
        var group = vm.NmCalibrationGroups.Should().ContainSingle().Subject;
        group.Rows.Should().HaveCount(6);
        group.IsCollapsed.Should().BeTrue("结构相同且结论一致，应折叠成一行");
        group.IsExpanded.Should().BeFalse("折叠组默认收起，只显示摘要行");
    }

    /// <summary>展开/收起命令必须真的切换状态。</summary>
    [Fact]
    public void ToggleNmCalibrationGroup_FlipsExpandedState()
    {
        var vm = NewVm();
        var rules = Enumerable.Range(1, 3)
            .Select(i => new NmRuleDef($"NM_R{i}", $"byte1.bit{i} 预留位", "normal",
                new NmRuleWhen("bit_rule", Byte: 1, Bit: i),
                new NmRuleExpect("is_zero"), "NM_RESERVED_BIT_SET")
            {
                Clauses = ["§5.4.3"],
            })
            .ToArray();
        vm.ApplyNmReport(Snapshot(Report(), Profile(rules: rules)));

        var group = vm.NmCalibrationGroups.Single();
        group.IsExpanded.Should().BeFalse();

        vm.ToggleNmCalibrationGroupCommand.Execute(group);
        group.IsExpanded.Should().BeTrue("点展开后逐条可见");
        group.ToggleText.Should().Be("收起");

        vm.ToggleNmCalibrationGroupCommand.Execute(group);
        group.IsExpanded.Should().BeFalse();
    }

    /// <summary>
    /// 组内有违规时不能默认收起——那等于把违规藏起来。
    /// </summary>
    [Fact]
    public void ApplyNmReport_GroupWithViolation_StillReportsViolationInHeader()
    {
        var vm = NewVm();
        var rules = Enumerable.Range(1, 3)
            .Select(i => new NmRuleDef($"NM_R{i}", $"byte1.bit{i} 预留位", "normal",
                new NmRuleWhen("bit_rule", Byte: 1, Bit: i),
                new NmRuleExpect("is_zero"), "NM_RESERVED_BIT_SET")
            {
                Clauses = ["§5.4.3"],
            })
            .ToArray();

        var report = Report(
            findings:
            [
                new NmFinding("NM_RESERVED_BIT_SET", "warn", "byte1.bit2 被置位",
                    [1.0], false, null, RuleId: "NM_R2"),
            ],
            evaluations:
            [
                new NmRuleEvaluation("NM_R1", NmRuleVerdict.EvaluatedNoFinding, null, []),
                new NmRuleEvaluation("NM_R2", NmRuleVerdict.EvaluatedWithFinding, null, []),
                new NmRuleEvaluation("NM_R3", NmRuleVerdict.EvaluatedNoFinding, null, []),
            ]);

        vm.ApplyNmReport(Snapshot(report, Profile(rules: rules)));

        var group = vm.NmCalibrationGroups.Single();
        group.HasViolation.Should().BeTrue("组内有触发，折叠头必须变红提示");
        group.IsCollapsed.Should().BeFalse("结论不一致时展开，不能把违规藏进折叠行");
        group.IsExpanded.Should().BeTrue("不折叠的组直接显示全部行");
    }

    /// <summary>
    /// 全「未建模」的组不折叠：面板特意把未建模排在未触发之前，
    /// 默认收成一行等于抵消「先看没查成的」这个意图。
    /// </summary>
    [Fact]
    public void ApplyNmReport_AllUnmodeledGroup_ExpandsByDefault()
    {
        var vm = NewVm();
        var rules = Enumerable.Range(1, 3)
            .Select(i => new NmRuleDef($"NM_R{i}", $"byte1.bit{i} 预留位", "normal",
                new NmRuleWhen("bit_rule", Byte: 1, Bit: i),
                new NmRuleExpect("is_zero"), "NM_RESERVED_BIT_SET") { Clauses = ["§5.4.3"] })
            .ToArray();

        var report = Report(
            unmodeled: rules.Select(r => new NmUnmodeledRule("§5.4.3", "缺 bit_roles")
            {
                RuleId = r.Id,
                ReasonCode = NmUnmodeledCodes.MissingProfileData,
            }).ToArray());

        vm.ApplyNmReport(Snapshot(report, Profile(rules: rules)));

        var group = vm.NmCalibrationGroups.Single();
        group.HasUnmodeled.Should().BeTrue("折叠头要变琥珀色，与单行「未建模」一致");
        group.IsCollapsed.Should().BeFalse("全未建模的组要展开");
        group.IsExpanded.Should().BeTrue();
    }

    /// <summary>
    /// <b>换报告时分组必须清空</b>：删掉 <c>NmCalibrationGroups.Clear()</c> 会让
    /// 旧组追加到新组后面，面板同时显示两份数据的折叠结论。
    /// <para>
    /// 这是上一轮刚修过的同类缺陷（换 trace 后面板残留上一份结论），
    /// 新集合不能重蹈覆辙。
    /// </para>
    /// </summary>
    [Fact]
    public void ApplyNmReport_ReplacesGroupsFromPreviousReport()
    {
        var vm = NewVm();
        var ruleA = new NmRuleDef("NM_A", "byte1.bit1 预留位", "normal",
            new NmRuleWhen("bit_rule", Byte: 1, Bit: 1),
            new NmRuleExpect("is_zero"), "NM_RESERVED_BIT_SET") { Clauses = ["§5.4.3"] };
        var ruleB = new NmRuleDef("NM_B", "byte1.bit2 预留位", "normal",
            new NmRuleWhen("bit_rule", Byte: 1, Bit: 2),
            new NmRuleExpect("is_zero"), "NM_RESERVED_BIT_SET") { Clauses = ["§5.4.3"] };

        vm.ApplyNmReport(Snapshot(Report(), Profile(rules: [ruleA, ruleB])));
        vm.NmCalibrationGroups.Should().ContainSingle();

        // 换成另一份报告：只有一条规则。
        vm.ApplyNmReport(Snapshot(Report(), Profile(rules: [ruleA])));

        vm.NmCalibrationGroups.Should().ContainSingle(
            "旧组必须被清掉，不能追加到新报告后面");
        vm.NmCalibrationGroups[0].Rows.Should().ContainSingle();
    }

    /// <summary>清空报告时分组也必须清掉。</summary>
    [Fact]
    public void ClearNmReport_ResetsCalibrationGroups()
    {
        var vm = NewVm();
        var rules = Enumerable.Range(1, 3)
            .Select(i => new NmRuleDef($"NM_R{i}", $"byte1.bit{i} 预留位", "normal",
                new NmRuleWhen("bit_rule", Byte: 1, Bit: i),
                new NmRuleExpect("is_zero"), "NM_RESERVED_BIT_SET") { Clauses = ["§5.4.3"] })
            .ToArray();
        vm.ApplyNmReport(Snapshot(Report(), Profile(rules: rules)));
        vm.NmCalibrationGroups.Should().NotBeEmpty();

        vm.ClearNmReportPublic();

        vm.NmCalibrationGroups.Should().BeEmpty();
    }

    /// <summary>清空报告时汇总也必须清掉，否则会残留上一份 trace 的计数。</summary>
    [Fact]
    public void ClearNmReport_ResetsCalibrationSummary()
    {
        var vm = NewVm();
        vm.ApplyNmReport(Snapshot(Report(), Profile(rules: [Rule("NM_05")])));
        vm.NmCalibrationSummary.Should().NotBeNullOrWhiteSpace();

        vm.ClearNmReportPublic();

        vm.NmCalibrationSummary.Should().BeEmpty();
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
