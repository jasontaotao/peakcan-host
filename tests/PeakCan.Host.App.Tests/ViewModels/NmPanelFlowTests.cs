using System.Collections.ObjectModel;
using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.Host.App.Converters;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.ChatTools;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using ScottPlot;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

public class NmPanelFlowTests
{
    // CA1861：常量数组字面量不能直接作为实参（TreatWarningsAsErrors）。
    private static readonly string[] CannmReasons = { "MsgType=CANNM" };
    private static readonly string[] UserSelectedReasons = { "UserSelected" };
    private static readonly string[] ConflictValues = { "500", "640" };
    private static readonly string[] ConflictSources = { "NmAsrCanMsgCycleTime", "GenMsgCycleTime" };
    private static readonly string[] MissingSelection = { "nm_message_selection" };
    private static readonly string[] NmParticipants = { "0x500" };
    private static readonly double[] HeadSilenceEvidence = { 0d, 4d };
    private static readonly double[] WholeWindowBoundaryEvidence = { 0d, 10d };

    [Fact]
    public void BuildTimeline_UsesDesignWidth1000()
    {
        var report = new NmReport(
            new NmWindow(0, 10, "full"), "src", null,
            Array.Empty<NmEpisode>(),
            new[]
            {
                new NmMessageReport("0x500", false, "NM", "A", 2, 2, 8,
                    new NmCycleStats(500, 500, 500, 500, 0, 0, 1, 500, 100),
                    new[] { new NmSegment(2, 8, "active") }),
            },
            Array.Empty<NmNodeReport>(), Array.Empty<NmEvent>(), Array.Empty<NmFinding>(),
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<NmCapabilityState>(),
            Array.Empty<NmInputMissing>(), Array.Empty<NmTruncation>());

        var rows = TraceViewerViewModel.BuildTimelineRows(report);

        var segment = Assert.Single(Assert.Single(rows).Segments);
        segment.Left.Should().BeApproximately(200, 0.001);
        segment.Width.Should().BeApproximately(600, 0.001);
    }

    [Fact]
    public void BuildCapabilityChips_ShowsMissingState()
    {
        var report = new NmReport(
            new NmWindow(0, 1, "full"), "src", null,
            Array.Empty<NmEpisode>(), Array.Empty<NmMessageReport>(), Array.Empty<NmNodeReport>(),
            Array.Empty<NmEvent>(), Array.Empty<NmFinding>(), Array.Empty<string>(),
            Array.Empty<string>(),
            new[]
            {
                new NmCapabilityState(NmCapability.NidCheck, false,
                    new[] { new NmInputMissing(NmCapability.NidCheck, "nid_byte", "why", "s", "measured") }),
            },
            Array.Empty<NmInputMissing>(), Array.Empty<NmTruncation>());

        var chips = TraceViewerViewModel.BuildCapabilityChips(report);

        chips.Should().ContainSingle();
        chips[0].Available.Should().BeFalse();
    }

    /// <summary>
    /// 汇总必须点出缺的是哪几项：「8/10 可用」本身不告诉用户缺什么，
    /// 用户还得展开折叠区才知道，等于把信息藏了两层。
    /// </summary>
    [Fact]
    public void BuildCapabilitySummary_NamesMissingCapabilities()
    {
        var chips = new[]
        {
            new NmCapabilityChipVm("NID", false, "缺 nid_byte"),
            new NmCapabilityChipVm("CBV", true, "可用"),
            new NmCapabilityChipVm("PN", false, "缺 pni_role"),
        };

        var summary = TraceViewerViewModel.BuildCapabilitySummary(chips);

        summary.Should().Contain("1/3", "要给出可用比例");
        summary.Should().Contain("NID").And.Contain("PN",
            "缺哪几项必须点名，否则用户还得展开才知道");
        summary.Should().NotContain("CBV", "可用项不必列出，那是噪音");
    }

    [Fact]
    public void BuildCapabilitySummary_AllAvailable_SaysSoWithoutNaming()
    {
        var chips = new[]
        {
            new NmCapabilityChipVm("NID", true, "可用"),
            new NmCapabilityChipVm("CBV", true, "可用"),
        };

        TraceViewerViewModel.BuildCapabilitySummary(chips)
            .Should().Be("能力 2/2 可用");
    }

    [Fact]
    public void BuildCapabilitySummary_Empty_IsEmptyString()
        => TraceViewerViewModel.BuildCapabilitySummary(Array.Empty<NmCapabilityChipVm>())
            .Should().BeEmpty("无能力信息时不该出现一条空白的折叠头");

    [Fact]
    public void BuildCapabilitySummary_AllMissing_SaysZeroOfN()
    {
        var chips = new[]
        {
            new NmCapabilityChipVm("NID", false, "缺 nid_byte"),
            new NmCapabilityChipVm("CBV", false, "缺 cbv_byte"),
        };

        var summary = TraceViewerViewModel.BuildCapabilitySummary(chips);

        summary.Should().Contain("0/2", "全不可用时也要给出比例，不能只列缺失项");
        summary.Should().Contain("NID").And.Contain("CBV");
    }

    /// <summary>
    /// 清空报告时必须把汇总一起清掉：留着上一份 trace 的能力汇总，
    /// 用户会以为新分析已经有结论。
    /// </summary>
    [Fact]
    public void ClearNmReport_ResetsCapabilitySummary()
    {
        var report = new NmReport(
            new NmWindow(0, 1, "full"), "src", null,
            Array.Empty<NmEpisode>(), Array.Empty<NmMessageReport>(), Array.Empty<NmNodeReport>(),
            Array.Empty<NmEvent>(), Array.Empty<NmFinding>(), Array.Empty<string>(),
            Array.Empty<string>(),
            new[]
            {
                new NmCapabilityState(NmCapability.NidCheck, false,
                    new[] { new NmInputMissing(NmCapability.NidCheck, "nid_byte", "why", "s", "measured") }),
            },
            Array.Empty<NmInputMissing>(), Array.Empty<NmTruncation>());
        var profile = new NmProfile(
            NmProfileSource.None, Array.Empty<NmMessageDef>(), new NmTimings(),
            Array.Empty<NmNodeDef>(), Array.Empty<NmConfigConflict>(), Array.Empty<string>(),
            new NmProvenance());
        var vm = NewVm();
        vm.ApplyNmReport(new NmToolSnapshot(
            "src", null, null, null, profile, report, Array.Empty<ReplayFrame>()));

        vm.NmCapabilitySummary.Should().NotBeNullOrWhiteSpace("前置条件：已有汇总");

        vm.ClearNmReportPublic();

        vm.NmCapabilitySummary.Should().BeEmpty(
            "清空后残留旧汇总，用户会以为新分析已经有结论");
    }

    [Fact]
    public void ApplyNmReport_NoMessages_ShowsHonestEmptyState()
    {
        var report = new NmReport(
            new NmWindow(0, 1, "full"), "src", null,
            Array.Empty<NmEpisode>(), Array.Empty<NmMessageReport>(), Array.Empty<NmNodeReport>(),
            Array.Empty<NmEvent>(), Array.Empty<NmFinding>(), Array.Empty<string>(),
            Array.Empty<string>(), Array.Empty<NmCapabilityState>(), Array.Empty<NmInputMissing>(),
            Array.Empty<NmTruncation>());
        var profile = new NmProfile(
            NmProfileSource.None, Array.Empty<NmMessageDef>(), new NmTimings(),
            Array.Empty<NmNodeDef>(), Array.Empty<NmConfigConflict>(), Array.Empty<string>(),
            new NmProvenance());
        var snapshot = new NmToolSnapshot(
            "src", null, null, null, profile, report, Array.Empty<ReplayFrame>());

        var vm = NewVm();
        vm.ApplyNmReport(snapshot);

        // spec §9.4：没有 NM 帧时只陈述事实，不得暗示"睡眠"/"掉线"等结论。
        vm.NmStatus.Should().Be("本窗口未发现 NM 帧");
        vm.NmStatus.Should().NotContain("睡眠");
        vm.NmStatus.Should().NotContain("掉线");
    }

    [Fact]
    public void EnsureSingleChannel_MultiChannelWithoutSelection_ThrowsWithChannelList()
    {
        var frames = new[]
        {
            new ReplayFrame(0, 0x500, 8, Array.Empty<byte>(), FrameFlags.None, Channel: 1),
            new ReplayFrame(0, 0x501, 8, Array.Empty<byte>(), FrameFlags.None, Channel: 0),
        };

        var act = () => TraceViewerViewModel.EnsureSingleChannel(frames, null);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*多通道 trace*channel: 0, 1*");
    }

    [Fact]
    public void EnsureSingleChannel_AllowsExplicitOrSingleChannel()
    {
        var frames = new[]
        {
            new ReplayFrame(0, 0x500, 8, Array.Empty<byte>(), FrameFlags.None, Channel: 0),
            new ReplayFrame(0, 0x501, 8, Array.Empty<byte>(), FrameFlags.None, Channel: 1),
        };

        var actExplicit = () => TraceViewerViewModel.EnsureSingleChannel(frames, 1);
        var actSingle = () => TraceViewerViewModel.EnsureSingleChannel(
            new[] { frames[0], frames[0] }, null);

        actExplicit.Should().NotThrow();
        actSingle.Should().NotThrow();
    }

    // ---------------- §6.6.6 sidecar 与 DBC 哈希 ----------------

    [Fact]
    public async Task GetNmSnapshot_StaleSidecar_DropsLayoutAndSelectorsAndReportsConflict()
    {
        const string dbcText = "VERSION \"\"\n\nBO_ 1280 NM_Message: 8 ECU_A\nBO_ 1536 CAN_MSG_B: 8 ECU_A\n";
        var dir = Path.Combine(Path.GetTempPath(), "nm-stale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var dbcPath = Path.Combine(dir, "net.dbc");
            File.WriteAllText(dbcPath, dbcText);
            var dbc = DbcParser.Parse(dbcText).Value! with { SourcePath = dbcPath };
            var selectors = new[] { "0x600" };
            NmSidecarStore.Save(dbcPath, new NmUserSupplement(
                NmMessageSelectors: selectors,
                NidByte: 0, CbvByte: 1,
                Timings: new NmTimings(CycleMs: 250),
                DbcHash: "deadbeef"));

            var vm = NewVmWithDbc(dbc, SampleFrames());
            var snapshot = await ((IChatToolContext)vm).GetNmSnapshotAsync(
                new NmToolQuery(SourceId: "src"), CancellationToken.None);

            // 布局与选择器都属于另一份 DBC：不得套用；timings 等其余字段照常合并。
            Assert.Null(snapshot.Profile.Layout);
            Assert.Equal(250, snapshot.Profile.Timings.CycleMs);
            Assert.False(snapshot.Profile.Messages.Single(m => m.CanIdHex == "0x600").IsNm);
            Assert.Contains(snapshot.Report.Assumptions, a => a.Contains("stale_sidecar"));

            // 冲突必须进入 profile 并转成 finding，面板黄色条才有数据。
            var conflict = Assert.Single(snapshot.Profile.Conflicts, c => c.Field == "dbc_hash");
            Assert.Equal("deadbeef", conflict.Values[0]);
            Assert.Equal(snapshot.DbcHash, conflict.Values[1]);
            var expectedSources = new[] { "sidecar", "dbc" };
            Assert.Equal(expectedSources, conflict.Sources);
            Assert.Contains(snapshot.Report.Findings,
                f => f.Code == "NM_CONFIG_CONFLICT" && f.Message.Contains("dbc_hash"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task GetNmSnapshot_HeavyHashAndSidecarWorkRunOffCallerThread()
    {
        const string dbcText = "VERSION \"\"\n\nBO_ 1280 NM_Message: 8 ECU_A\n";
        var dir = Path.Combine(Path.GetTempPath(), "nm-thread-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var dbcPath = Path.Combine(dir, "net.dbc");
            File.WriteAllText(dbcPath, dbcText);
            var dbc = DbcParser.Parse(dbcText).Value! with { SourcePath = dbcPath };

            var vm = NewVmWithDbc(dbc, SampleFrames());
            Thread? probeThread = null;
            vm.NmHeavyWorkProbe = () => probeThread = Thread.CurrentThread;
            var callerThread = Thread.CurrentThread;

            await ((IChatToolContext)vm).GetNmSnapshotAsync(
                new NmToolQuery(SourceId: "src"), CancellationToken.None);

            // 哈希/sidecar 读盘发生在 Task.Run 内，不能占用调用（UI）线程。
            Assert.NotNull(probeThread);
            Assert.NotEqual(callerThread.ManagedThreadId, probeThread!.ManagedThreadId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task GetNmSnapshot_SidecarWithoutHash_AppliesLayout()
    {
        const string dbcText = "VERSION \"\"\n\nBO_ 1280 NM_Message: 8 ECU_A\n";
        var dir = Path.Combine(Path.GetTempPath(), "nm-fresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var dbcPath = Path.Combine(dir, "net.dbc");
            File.WriteAllText(dbcPath, dbcText);
            var dbc = DbcParser.Parse(dbcText).Value! with { SourcePath = dbcPath };
            NmSidecarStore.Save(dbcPath, new NmUserSupplement(NidByte: 0, CbvByte: 1));

            var vm = NewVmWithDbc(dbc, SampleFrames());
            var snapshot = await ((IChatToolContext)vm).GetNmSnapshotAsync(
                new NmToolQuery(SourceId: "src"), CancellationToken.None);

            Assert.Equal((byte?)0, snapshot.Profile.Layout!.NidByte);
            Assert.DoesNotContain(snapshot.Report.Assumptions,
                a => a.Contains("stale_sidecar"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static ReplayFrame[] SampleFrames()
        => new[]
        {
            new ReplayFrame(0, 0x500, 8, new byte[] { 0xF4, 0, 0, 0, 0, 0, 0, 0 }, FrameFlags.None),
            new ReplayFrame(0.5, 0x500, 8, new byte[] { 0xF4, 0, 0, 0, 0, 0, 0, 0 }, FrameFlags.None),
        };

    private static TraceViewerViewModel NewVmWithDbc(
        DbcDocument dbc, IReadOnlyList<ReplayFrame> frames)
    {
        var session = Substitute.For<ITraceSessionService>();
        session.WatchedSignals.Returns(new ObservableCollection<WatchedSignalRow>());
        session.SignalGroups.Returns(new ObservableCollection<WatchedSignalGroup>());
        var registry = Substitute.For<ITraceSessionRegistry>();
        var viewer = Substitute.For<ITraceViewerService>();
        viewer.LoadedFrames.Returns(frames);
        registry.GetService("src").Returns(viewer);
        var dbcService = new DbcService(NullLogger<DbcService>.Instance);
        dbcService.SetCurrentForTests(dbc);
        var libPath = Path.Combine(Path.GetTempPath(), $"nm-panel-{Guid.NewGuid():N}.tmtrace");
        return new TraceViewerViewModel(session, registry, dbcService,
            NullLogger<TraceViewerViewModel>.Instance,
            new TraceSessionLibrary(libPath, NullLogger<TraceSessionLibrary>.Instance));
    }

    // ---------------- §9.1 面板交互 ----------------

    [Fact]
    public void BuildTimelineRows_PreservesKindAndAddsTooltip()
    {
        var report = MakeReport(messages: new[]
        {
            new NmMessageReport("0x500", false, "NM", "A", 4, 0, 10,
                new NmCycleStats(500, 500, 500, 500, 0, 0, 1, 500, 100),
                new[]
                {
                    new NmSegment(0, 2, "wakeup"),
                    new NmSegment(2, 8, "active"),
                    new NmSegment(8, 9, "released"),
                    new NmSegment(9, 10, "silent"),
                }),
        });

        var row = Assert.Single(TraceViewerViewModel.BuildTimelineRows(report));

        // 每报文一行（spec §9.1），Kind 原样保留给着色转换器。
        row.Segments.Select(s => s.Kind).Should().Equal("wakeup", "active", "released", "silent");
        // 面向用户的 tooltip 必须是中文，内部 kind 不作为文案泄漏。
        row.Segments[0].Tooltip.Should().Contain("唤醒");
        row.Segments[0].Tooltip.Should().NotContain("wakeup");
        row.Segments[0].Tooltip.Should().Contain("0.000");
    }

    [Theory]
    [InlineData(NmCapability.IdentifyMessages, "识别")]
    [InlineData(NmCapability.Timing, "周期")]
    [InlineData(NmCapability.Activity, "活动")]
    [InlineData(NmCapability.RawContent, "原始内容")]
    [InlineData(NmCapability.SignalDecode, "信号解码")]
    [InlineData(NmCapability.NidCheck, "NID")]
    [InlineData(NmCapability.CbvDecode, "CBV")]
    [InlineData(NmCapability.PnCheck, "PN")]
    [InlineData(NmCapability.UserData, "用户数据")]
    [InlineData(NmCapability.OemRules, "企标")]
    public void CapabilityShortName_UsesChineseLabel(NmCapability capability, string expected)
        => TraceViewerViewModel.CapabilityShortName(capability).Should().Be(expected);

    [Fact]
    public void BuildCapabilityChips_UsesChineseShortName()
    {
        var report = MakeReport(capabilities: new[]
        {
            new NmCapabilityState(NmCapability.IdentifyMessages, true, Array.Empty<NmInputMissing>()),
            new NmCapabilityState(NmCapability.NidCheck, false, Array.Empty<NmInputMissing>()),
        });

        var chips = TraceViewerViewModel.BuildCapabilityChips(report);

        chips.Select(c => c.Name).Should().Equal("识别", "NID");
        chips[1].Available.Should().BeFalse();
    }

    [Fact]
    public void BuildReasonsSummary_GroupsCountsAndMapsUserSelected()
    {
        var messages = new[]
        {
            new NmMessageDef("0x500", false, "NM1", "A", 8, true, CannmReasons),
            new NmMessageDef("0x501", false, "NM2", "B", 8, true, CannmReasons),
            new NmMessageDef("0x502", false, "NM3", "C", 8, true, UserSelectedReasons),
        };

        TraceViewerViewModel.BuildReasonsSummary(messages)
            .Should().Be("MsgType=CANNM ×2 / 用户勾选 ×1");

        // Profile 条必须真的展示这行依据，而不只是纯函数能算。
        var vm = NewVm();
        vm.ApplyNmReport(MakeSnapshot(MakeReport(),
            MakeProfile(messages: messages)));
        // 360px 宽度下依据单独成行：主摘要只留来源/报文数/周期/超时。
        vm.NmProfileReasonText.Should().Be("识别依据 MsgType=CANNM ×2 / 用户勾选 ×1");
        vm.NmProfileSummary.Should().NotContain("识别依据");
    }

    [Fact]
    public void ApplyNmReport_Partial_SetsBadgeAndStatus()
    {
        var snapshot = MakeSnapshot(
            MakeReport(isPartial: true),
            MakeProfile());

        var vm = NewVm();
        vm.ApplyNmReport(snapshot);

        vm.NmIsPartial.Should().BeTrue();
        // 直接应用 partial 报告时无法得知是取消还是超时；默认按取消措辞，
        // 具体原因由 ResolveStatus 用例覆盖。
        vm.NmStatus.Should().Contain("部分结果");
        vm.NmPartialText.Should().Contain("取消");
        vm.NmEventFilterSummary.Should().Be("显示 0 / 共 0");
    }

    [Fact]
    public void ApplyNmReport_EventFilter_RebuildsRowsAndSummary()
    {
        var report = MakeReport(events: new[]
        {
            new NmEvent(1, "burst_observed", null, "0x500", "短间隔", 0, "measured"),
            new NmEvent(2, "byte_change", null, "0x500", "byte0 变化", 0, "measured"),
        });

        var vm = NewVm();
        vm.ApplyNmReport(MakeSnapshot(report, MakeProfile()));
        vm.NmEventRows.Should().HaveCount(2);
        vm.NmEventFilterSummary.Should().Be("显示 2 / 共 2");

        vm.NmSelectedEventTypeFilter = "字节";
        vm.NmEventRows.Should().ContainSingle().Which.Type.Should().Be("byte_change");
        vm.NmEventFilterSummary.Should().Be("显示 1 / 共 2");

        vm.NmEventKeyword = "不存在";
        vm.NmEventRows.Should().BeEmpty();
        vm.NmEventFilterSummary.Should().Be("显示 0 / 共 2");
    }

    [Fact]
    public void ApplyNmReport_NoDbc_ShowsLoadDbcHint()
    {
        var report = MakeReport(missing: new[]
        {
            new NmInputMissing(NmCapability.IdentifyMessages, "dbc", "没有加载 DBC，无法读取 NM 属性"),
        });
        var vm = NewVm();

        vm.ApplyNmReport(MakeSnapshot(report,
            MakeProfile(source: NmProfileSource.None)));

        vm.NmStatus.Should().Be("请先加载 DBC（NM 分析需要 DBC 约束）");
    }

    [Fact]
    public void ApplyNmReport_DbcWithoutNmConfig_ShowsNoConfigHint()
    {
        var report = MakeReport(missing: new[]
        {
            new NmInputMissing(NmCapability.IdentifyMessages, "nm_message_selection", "无 NM 标记"),
        });
        var profile = MakeProfile(
            source: NmProfileSource.Dbc,
            messages: new[]
            {
                new NmMessageDef("0x500", false, "App", "A", 8, false, Array.Empty<string>()),
            },
            missingFields: MissingSelection);

        var vm = NewVm();
        vm.ApplyNmReport(MakeSnapshot(report, profile));

        vm.NmStatus.Should().Be("该 DBC 未包含 NM 配置");
        vm.NmStatus.Should().NotContain("本窗口未发现");
    }

    [Fact]
    public void ApplyNmReport_Conflicts_PopulatesYellowBar()
    {
        var profile = MakeProfile(conflicts: new[]
        {
            new NmConfigConflict("CycleMs", ConflictValues, ConflictSources),
        });

        var vm = NewVm();
        vm.ApplyNmReport(MakeSnapshot(MakeReport(), profile));

        vm.NmHasConflicts.Should().BeTrue();
        vm.NmConflictLines.Should().ContainSingle()
            .Which.Should().Contain("CycleMs").And.Contain("500").And.Contain("640")
            .And.Contain("GenMsgCycleTime");
    }

    [Fact]
    public void ApplyNmReport_NoConflicts_HidesYellowBar()
    {
        var vm = NewVm();
        vm.ApplyNmReport(MakeSnapshot(MakeReport(), MakeProfile()));

        vm.NmHasConflicts.Should().BeFalse();
        vm.NmConflictLines.Should().BeEmpty();
    }

    [Fact]
    public void NmEventFilters_FilterByCategoryAndKeyword()
    {
        var rows = new[]
        {
            new NmEventRowVm(1, "1", "burst_observed", "0x500", "短间隔 4 帧", "measured", 0),
            new NmEventRowVm(2, "2", "byte_change", "0x500", "byte0 变化", "measured", 0),
            new NmEventRowVm(3, "3", "sender_silent", "ECU_A", "2.20s", "inferred", 0),
            new NmEventRowVm(4, "4", "missed_frame", "ECU_B", "可能漏发 1 帧", "inferred", 0),
        };

        NmEventFilters.Filter(rows, "突发", null).Should().ContainSingle()
            .Which.Type.Should().Be("burst_observed");
        NmEventFilters.Filter(rows, "空档", null).Should().ContainSingle()
            .Which.Type.Should().Be("sender_silent");
        NmEventFilters.Filter(rows, "字节", null).Should().ContainSingle()
            .Which.Type.Should().Be("byte_change");
        NmEventFilters.Filter(rows, "漏帧", null).Should().ContainSingle()
            .Which.Type.Should().Be("missed_frame");
        // M1 没有 cycle_* 事件；「周期」在现有契约下命中漏帧（间隔异常），
        // 未来 cycle_deviation 也归入该分类。
        NmEventFilters.MatchesCategory("cycle_deviation", "周期").Should().BeTrue();
        NmEventFilters.MatchesCategory("missed_frame", "周期").Should().BeTrue();
        NmEventFilters.MatchesCategory("byte_change", "周期").Should().BeFalse();
        NmEventFilters.Filter(rows, "周期", null).Should().ContainSingle()
            .Which.Type.Should().Be("missed_frame");
        NmEventFilters.Filter(rows, NmEventFilters.All, "ecu_a").Should().ContainSingle()
            .Which.Subject.Should().Be("ECU_A");
        NmEventFilters.Filter(rows, NmEventFilters.All, "byte0").Should().ContainSingle()
            .Which.Type.Should().Be("byte_change");
        NmEventFilters.Filter(rows, NmEventFilters.All, null).Should().HaveCount(4);
    }

    [Fact]
    public async Task RunNmAnalysisAsync_SetsAnalyzingFalseAndHasReportTrue()
    {
        const string dbcText = "VERSION \"\"\n\nBO_ 1280 NM_Message: 8 ECU_A\n";
        var dbc = DbcParser.Parse(dbcText).Value!;
        var vm = NewVmWithSource(dbc, SampleFrames());

        await vm.RunNmAnalysisAsync(null);

        vm.NmIsAnalyzing.Should().BeFalse();
        vm.NmCanCancel.Should().BeFalse();
        vm.NmHasReport.Should().BeTrue();
        // 该 DBC 无任何 NM 属性：面板必须直接说明，而不是“未发现 NM 帧”。
        vm.NmStatus.Should().Be("该 DBC 未包含 NM 配置");
    }

    [Fact]
    public void StoreNmReport_PartialIsNotCached_FullIsCached()
    {
        var vm = NewVm();
        var key = new NmCacheKey(
            "src", null, 0, 10, "full", 10, 1, "profile-hash", NmAnalyzer.EngineVersion);

        vm.StoreNmReport(key, MakeReport(isPartial: true));
        vm.TryGetCachedNmReport(key, out _).Should().BeFalse("partial 结果不得进入缓存（spec §9.4）");

        vm.StoreNmReport(key, MakeReport());
        vm.TryGetCachedNmReport(key, out var cached).Should().BeTrue();
        cached.IsPartial.Should().BeFalse();
    }

    [Fact]
    public void SeekToNmEventCommand_SeeksMasterService()
    {
        var viewer = Substitute.For<ITraceViewerService>();
        var registry = Substitute.For<ITraceSessionRegistry>();
        registry.Sources.Returns(new List<TraceSource>
        {
            new("seek-src", "t", "C:/t.asc", Colors.Blue, new LineStyle()),
        });
        registry.GetService("seek-src").Returns(viewer);
        var seekVm = NewVmWithRegistry(registry);

        seekVm.SeekToNmEventCommand.Execute(
            new NmEventRowVm(3.5, "3.500", "burst_observed", "0x500", "d", "measured", 0));

        viewer.Received(1).Seek(3.5);
    }

    [Fact]
    public void InterpretNmEvent_PrefillsChatInputWithoutSending()
    {
        var vm = NewVm();
        vm.NmSelectedEvent = new NmEventRowVm(
            2.5, "2.500", "burst_observed", "ECU_A", "3 帧短间隔", "measured", 0, "0x500");

        vm.InterpretNmEventInAiCommand.CanExecute(null).Should().BeTrue();
        vm.InterpretNmEventInAiCommand.Execute(null);

        vm.ChatInput.Should().Contain("时间: 2.500");
        vm.ChatInput.Should().Contain("报文 ID: 0x500");
        vm.ChatInput.Should().Contain("对象: ECU_A");
        vm.ChatInput.Should().Contain("类型: burst_observed");
        vm.ChatInput.Should().Contain("详情: 3 帧短间隔");
        vm.ChatInput.Should().Contain("证据级别: measured");
        vm.ChatInput.Should().Contain("结果状态: 完整");
        // 不得自动发送：聊天记录仍为空。
        vm.ChatMessages.Should().BeEmpty();
    }

    [Fact]
    public void InterpretNmEvent_PartialPromptMarksResultState()
    {
        var prompt = TraceViewerViewModel.BuildNmEventAiPrompt(
            new NmEventRowVm(1, "1.000", "sender_silent", "ECU_A", "2s", "inferred", 0, "0x500"),
            isPartial: true);

        prompt.Should().Contain("结果状态: 部分结果（分析已取消）");
    }

    [Fact]
    public void InterpretNmEvent_RedactsRawPayloadBeforePrefill()
    {
        var vm = NewVm();
        vm.NmSelectedEvent = new NmEventRowVm(
            1.5, "1.500", "byte_change", "0x500",
            "byte0: 0x00 → 0x08，变化 3 次（1.100s → 1.400s）", "measured", 0, "0x500");

        vm.InterpretNmEventInAiCommand.Execute(null);

        // spec §11：面板本地显示原文，但发给 LLM 的提示必须脱敏（同 get_nm_events）。
        vm.ChatInput.Should().NotContain("0x00 → 0x08");
        vm.ChatInput.Should().Contain("0x?? → 0x??");
        vm.ChatInput.Should().Contain("变化 3 次");
    }

    [Fact]
    public void InterpretNmEvent_ReasonChange_RedactsRawValues()
    {
        var vm = NewVm();
        vm.NmSelectedEvent = new NmEventRowVm(
            1.5, "1.500", "reason_change", "0x18FFFAF4",
            "BMS_Wakeup_reasons: 0x0 → 0x30（Waked up by NW PDU）", "measured", 0, "0x18FFFAF4");

        vm.InterpretNmEventInAiCommand.Execute(null);

        // spec §11：原始取值不发 LLM；信号名与解释文本保留。
        vm.ChatInput.Should().NotContain("0x0 →").And.NotContain("0x30");
        vm.ChatInput.Should().Contain("0x??").And.Contain("Waked up by NW PDU");
    }

    [Fact]
    public void InterpretNmEvent_SwitchesToChatTabAndShowsHint()
    {
        var vm = NewVm();
        vm.NmSelectedEvent = new NmEventRowVm(
            2.5, "2.500", "burst_observed", "ECU_A", "3 帧短间隔", "measured", 0, "0x500");

        vm.InterpretNmEventInAiCommand.Execute(null);

        // 点按钮后必须把用户带到 AI 对话页，否则预填没有任何可见反馈。
        vm.RightPanelSelectedIndex.Should().Be(TraceViewerViewModel.ChatPanelTabIndex);
        vm.ChatPrefillHint.Should().Contain("按发送");
    }

    [Fact]
    public void SummarizeNmInAi_PrefillsOverviewPromptAndSwitchesTab()
    {
        var vm = NewVm();
        vm.ApplyNmReport(MakeSnapshot(MakeReport(), MakeProfile()));

        vm.SummarizeNmInAiCommand.CanExecute(null).Should().BeTrue();
        vm.SummarizeNmInAiCommand.Execute(null);

        // 总结必须以工具输出为准：提示里显式要求 AI 调 overview，而不是凭记忆汇总。
        vm.ChatInput.Should().Contain("get_nm_overview");
        // 面板可能只分析了某个窗口/通道：AI 重新取数必须沿用同一组参数。
        vm.ChatInput.Should().Contain("source_id=src");
        vm.ChatInput.Should().Contain("window_ref=full");
        vm.RightPanelSelectedIndex.Should().Be(TraceViewerViewModel.ChatPanelTabIndex);
        vm.ChatPrefillHint.Should().Contain("按发送");
        vm.ChatMessages.Should().BeEmpty();
    }

    [Fact]
    public void SummarizeNmInAi_RefreshesCanExecuteWhenReportChanges()
    {
        var vm = NewVm();
        var raised = 0;
        vm.SummarizeNmInAiCommand.CanExecuteChanged += (_, _) => raised++;

        vm.ApplyNmReport(MakeSnapshot(MakeReport(), MakeProfile()));

        // WPF 按钮只在 CanExecuteChanged 后重查 CanExecute；没有这层通知，
        // 按钮会一直保持禁用，再正确的 CanExecute 也点不到。
        raised.Should().BeGreaterThan(0);
        vm.SummarizeNmInAiCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void NmAiJump_IncrementsFocusTicketEachTime()
    {
        var vm = NewVm();
        vm.NmSelectedEvent = new NmEventRowVm(
            1.0, "1.000", "burst_observed", "A", "d", "measured", 0, "0x500");

        vm.InterpretNmEventInAiCommand.Execute(null);
        vm.ChatInputFocusTicket.Should().Be(1);

        // 连续调用必须再次递增：用户可能改了输入但没发送，再点一次仍要聚焦。
        vm.InterpretNmEventInAiCommand.Execute(null);
        vm.ChatInputFocusTicket.Should().Be(2);
    }

    [Fact]
    public async Task NmTabHeader_ResetsWhenAnalysisFails()
    {
        var vm = NewVm();
        vm.ApplyNmReport(MakeSnapshot(
            MakeNetworkReport(findings: new[]
            {
                new NmFinding("NM_CYCLE_DEVIATION", "warn", "a", Array.Empty<double>(), true, null),
            }),
            MakeProfile()));
        vm.NmTabHeader.Should().Be("网络管理 (1)");

        // NewVm 的 registry 没有 source：分析在快照阶段失败 → ClearNmReport。
        await vm.RunNmAnalysisAsync(null);

        vm.NmTabHeader.Should().Be("网络管理", "报告清空后 Tab 徽标不能残留");
        vm.NmHasReport.Should().BeFalse();
        vm.NmProfileSummary.Should().BeEmpty("失败后不能残留上一次报告的摘要");
        vm.NmProfileReasonText.Should().BeEmpty("失败后不能残留上一次报告的识别依据");
    }

    [Fact]
    public async Task SendMessage_WithoutProvider_ClearsPrefillHint()
    {
        var vm = NewVm();
        vm.ChatInput = "问题";
        vm.ChatPrefillHint = "已填入问题，按发送开始";

        await vm.SendMessageCommand.ExecuteAsync(null);

        // 用户已经按了发送：提示不能继续停留在“按发送开始”的待办状态。
        vm.ChatPrefillHint.Should().BeEmpty();
    }

    [Fact]
    public void SummarizeNmInAi_RequiresReport()
    {
        var vm = NewVm();

        vm.SummarizeNmInAiCommand.CanExecute(null).Should().BeFalse();

        vm.ApplyNmReport(MakeSnapshot(MakeReport(), MakeProfile()));

        vm.SummarizeNmInAiCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void NmTabHeader_ShowsWarnFindingCount()
    {
        var vm = NewVm();
        vm.ApplyNmReport(MakeSnapshot(
            MakeNetworkReport(findings: new[]
            {
                new NmFinding("NM_CYCLE_DEVIATION", "warn", "a", Array.Empty<double>(), true, null),
                new NmFinding("NM_NODE_DROPOUT", "warn", "b", Array.Empty<double>(), true, null),
                new NmFinding("NM_TRACE_BOUNDARY", "info", "c", Array.Empty<double>(), false, null),
            }),
            MakeProfile()));

        // info 噪声不算：只有 warn 才代表“需要用户看一眼”。
        vm.NmTabHeader.Should().Be("网络管理 (2)");
    }

    [Fact]
    public void NmTabHeader_WithoutWarn_ShowsPlainName()
    {
        var vm = NewVm();
        vm.ApplyNmReport(MakeSnapshot(MakeReport(), MakeProfile()));

        vm.NmTabHeader.Should().Be("网络管理");
    }

    [Fact]
    public void BuildNmSummaryAiPrompt_PartialMarksState()
    {
        var prompt = TraceViewerViewModel.BuildNmSummaryAiPrompt(
            MakeSnapshot(MakeReport(isPartial: true), MakeProfile()));

        prompt.Should().Contain("部分结果");
        prompt.Should().Contain("get_nm_overview");
    }

    [Fact]
    public void ShouldAutoOpenNmWizard_WithMissingSelectionAndGlobalAttributes_ReturnsTrue()
    {
        var profile = MakeProfile(
            timings: new NmTimings(CycleMs: 500, TimeoutMs: 2000),
            missingFields: MissingSelection);
        var report = MakeReport(missing: new[]
        {
            new NmInputMissing(NmCapability.IdentifyMessages, "nm_message_selection", "无标记"),
        });

        TraceViewerViewModel.ShouldAutoOpenNmWizard(MakeSnapshot(report, profile, "hash1"))
            .Should().BeTrue();
    }

    [Fact]
    public void ShouldAutoOpenNmWizard_NoGlobalAttributes_ReturnsFalse()
    {
        var profile = MakeProfile(source: NmProfileSource.Dbc, missingFields: MissingSelection);
        var report = MakeReport(missing: new[]
        {
            new NmInputMissing(NmCapability.IdentifyMessages, "nm_message_selection", "无标记"),
        });

        TraceViewerViewModel.ShouldAutoOpenNmWizard(MakeSnapshot(report, profile, "hash1"))
            .Should().BeFalse();
    }

    [Fact]
    public void ShouldAutoOpenNmWizard_PartialReport_ReturnsFalse()
    {
        var profile = MakeProfile(
            timings: new NmTimings(CycleMs: 500, TimeoutMs: 2000),
            missingFields: MissingSelection);
        var report = MakeReport(
            missing: new[]
            {
                new NmInputMissing(NmCapability.IdentifyMessages, "nm_message_selection", "无标记"),
            },
            isPartial: true);

        TraceViewerViewModel.ShouldAutoOpenNmWizard(MakeSnapshot(report, profile, "hash1"))
            .Should().BeFalse();
    }

    [Fact]
    public void ClaimNmWizardAutoOpen_DeduplicatesByDbcHash()
    {
        var vm = NewVm();

        vm.ClaimNmWizardAutoOpen("hash1").Should().BeTrue();
        vm.ClaimNmWizardAutoOpen("hash1").Should().BeFalse("同一 dbc_hash 只自动弹一次");
        vm.ClaimNmWizardAutoOpen("hash2").Should().BeTrue();
        vm.ClaimNmWizardAutoOpen("inline").Should().BeTrue("内存 DBC 也参与会话内去重");
        vm.ClaimNmWizardAutoOpen("inline").Should().BeFalse();
        vm.ClaimNmWizardAutoOpen(null).Should().BeFalse();
        vm.ClaimNmWizardAutoOpen("none").Should().BeFalse();
    }

    // ---------------- 测试辅助 ----------------

    [Fact]
    public void NewPanel_ShowsReadableWizardButtonLabel()
    {
        // 分析前按钮文案不能为空，否则空按钮在 DockPanel 里会被压成一条缝。
        NewVm().NmMissingText.Should().Be("补全");
    }

    // ==== spec §9.1：时间条事件标记 / 网络级静默与边界 ====

    [Fact]
    public void BuildTimelineRows_MarksEventsAndNetworkSilence()
    {
        var report = new NmReport(
            new NmWindow(0, 10, "full"), "src", null,
            new[]
            {
                new NmEpisode(0, 0, 4, "active", null, NmParticipants, false, true),
                new NmEpisode(1, 7, 10, "active", null, NmParticipants, true, false),
            },
            new[]
            {
                new NmMessageReport("0x500", false, "NM", "A", 4, 0, 10,
                    new NmCycleStats(500, 500, 500, 500, 0, 0, 3, 500, 100),
                    new[] { new NmSegment(0, 10, "active") }),
            },
            Array.Empty<NmNodeReport>(),
            new[]
            {
                new NmEvent(0.5, "burst_observed", "A", "0x500", "3 帧短间隔", 0, "measured"),
                new NmEvent(1.0, "byte_change", null, "0x500", "byte0: 0x00 → 0x08", 0, "measured"),
                new NmEvent(2.0, "sender_silent", null, "0x500", "1.0s", 0, "inferred"),
                new NmEvent(4.0, "all_nm_silent", null, null, "NM silent until 7.000s", 0, "measured"),
            },
            Array.Empty<NmFinding>(), Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<NmCapabilityState>(), Array.Empty<NmInputMissing>(),
            Array.Empty<NmTruncation>());

        var rows = TraceViewerViewModel.BuildTimelineRows(report);

        rows.Should().HaveCount(2);
        // 事件标记独立成轨道：不再混进活动段（否则 3px 标记被 Viewbox 压成 1px）。
        var messageMarkers = rows[0].Markers.Select(m => m.Kind).ToArray();
        messageMarkers.Should().Contain("burst");
        messageMarkers.Should().Contain("byte");
        messageMarkers.Should().Contain("gap");
        rows[0].Segments.Select(s => s.Kind).Should().Equal("active");
        rows[0].Markers.Should().OnlyContain(m => m.Tooltip.Contains('s'));

        // 网络行：episode 活动段 + 4s–7s 静默段 + 边界标记。
        rows[1].Label.Should().Be("整网状态");
        var networkKinds = rows[1].Segments.Select(s => s.Kind).ToArray();
        networkKinds.Should().Contain("episode");
        networkKinds.Should().Contain("silence");
        rows[1].Markers.Select(m => m.Kind).Should().Contain("boundary")
            .And.NotContain("silence", "静默是区间不是标记");
        var silence = rows[1].Segments.Single(s => s.Kind == "silence");
        silence.Start.Should().Be(4.0);
        silence.End.Should().Be(7.0);
        silence.Tooltip.Should().Contain("无 NM 报文");
    }

    [Fact]
    public void BuildNetworkTimelineRow_BusWideSilenceInsideEpisode_IsMarkerNotBand()
    {
        // 全网断帧 5s 但短于 NM 超时 → episode 不切分，事件落在 [0,10] 内部。
        // 旧实现把 2s→窗口末尾涂成静默，覆盖了 2s–10s 的真实活动段。
        var report = MakeNetworkReport(
            episodes: new[] { new NmEpisode(0, 0, 10, "active", null, NmParticipants, false, false) },
            events: new[] { new NmEvent(2.0, "bus_wide_silence", null, null, "5.00s", 0, "measured") });

        var network = Assert.Single(TraceViewerViewModel.BuildTimelineRows(report));

        // 静默带与 episode 无交集时必须整段丢弃，不能覆盖活动区间。
        network.Segments.Select(s => s.Kind).Should().Equal("episode");
        var marker = Assert.Single(network.Markers);
        marker.Kind.Should().Be("bus_gap");
        marker.X.Should().BeApproximately(200, 0.001);
        marker.Tooltip.Should().Contain("总线无帧");
    }

    [Fact]
    public void BuildNetworkTimelineRow_BusWideSilenceInsideLastEpisode_IsMarkerOnly_NoTailBand()
    {
        // 尾部 episode 到窗口终点，内部 1s 处有总线断帧；尾段静默 1s < 超时，
        // 引擎不会产 all_nm_silent。旧实现用「到窗口末尾」补区间，凭空画出
        // 9–10s 静默带；该残段与这次断帧无关，必须丢弃。
        var report = MakeNetworkReport(
            episodes: new[]
            {
                new NmEpisode(0, 0, 9, "active", null, NmParticipants, false, true),
            },
            events: new[] { new NmEvent(1.0, "bus_wide_silence", null, null, "1.00s", 0, "measured") });

        var network = Assert.Single(TraceViewerViewModel.BuildTimelineRows(report));

        network.Segments.Select(s => s.Kind).Should().Equal("episode");
        network.Segments.Should().NotContain(s => s.Kind == "silence");
        network.Markers.Select(m => m.Kind).Should().Contain("bus_gap")
            .And.Contain("boundary");
    }

    [Fact]
    public void BuildNetworkTimelineRow_BusWideSilenceAtEpisodeStart_IsMarkerOnly()
    {
        // 断帧起点恰好等于 episode 首帧时刻（T == episode.Start）：仍属 episode
        // 内部，不能当作「episode 之间」去补尾段静默带。
        var report = MakeNetworkReport(
            window: new NmWindow(0, 12, "full"),
            episodes: new[]
            {
                new NmEpisode(0, 10, 11.5, "active", null, NmParticipants, false, true),
            },
            events: new[] { new NmEvent(10.0, "bus_wide_silence", null, null, "1.00s", 0, "measured") });

        var network = Assert.Single(TraceViewerViewModel.BuildTimelineRows(report));

        network.Segments.Select(s => s.Kind).Should().Equal("episode");
        network.Markers.Select(m => m.Kind).Should().Contain("bus_gap");
    }

    [Fact]
    public void SubtractEpisodes_RemovesCoveredPiecesAndKeepsResiduals()
    {
        var episodes = new[]
        {
            new NmEpisode(0, 0, 3, "active", null, NmParticipants, false, false),
            new NmEpisode(1, 5, 6, "active", null, NmParticipants, false, false),
        };

        var pieces = TraceViewerViewModel.SubtractEpisodes(2, 8, episodes);

        pieces.Should().Equal((3.0, 5.0), (6.0, 8.0));
        TraceViewerViewModel.SubtractEpisodes(1, 2, episodes).Should().BeEmpty(
            "完全被 episode 覆盖的区间不能留下零宽残段");
        // episode 之间的空隙不属于任何 episode，必须保留。
        TraceViewerViewModel.SubtractEpisodes(3, 5, episodes).Should().Equal((3.0, 5.0));
    }

    [Fact]
    public void MergeSilenceBands_OverlappingHeadAndEventBand_KeepsEarliestTooltip()
    {
        var merged = TraceViewerViewModel.MergeSilenceBands(new[]
        {
            (1.0, 4.0, "无 NM 报文"),
            (0.0, 4.0, "记录边界"),
            (5.0, 6.0, "无 NM 报文"),
        });

        merged.Should().HaveCount(2);
        merged[0].Start.Should().Be(0.0);
        merged[0].End.Should().Be(4.0);
        merged[0].Tooltip.Should().Be("记录边界", "起点更早的头部边界说明优先");
        merged[1].Start.Should().Be(5.0);
    }

    [Fact]
    public void BuildNetworkTimelineRow_HeadSilenceAndBusGap_MergeIntoSingleBand()
    {
        // 头部静默 [0,4] 与总线断帧派生的 [1,4] 重叠：只能画一条带，
        // 否则半透明灰叠深、且同一段时间有两个可点区间。
        var report = MakeNetworkReport(
            episodes: new[] { new NmEpisode(0, 4, 9, "active", null, NmParticipants, false, false) },
            events: new[] { new NmEvent(1.0, "bus_wide_silence", null, null, "2.00s", 0, "measured") },
            findings: new[]
            {
                new NmFinding("NM_TRACE_BOUNDARY", "info", "窗口开头静默", HeadSilenceEvidence,
                    false, null),
            });

        var network = Assert.Single(TraceViewerViewModel.BuildTimelineRows(report));

        var silence = network.Segments.Where(s => s.Kind == "silence").Should().ContainSingle().Which;
        silence.Start.Should().Be(0.0);
        silence.End.Should().Be(4.0);
        silence.Tooltip.Should().Contain("记录边界");
    }

    [Fact]
    public void BuildTimelineRows_EmitsOnlyDeclaredKinds()
    {
        // 生产者覆盖守卫：转换器测试只能证明常量表都有笔刷；这里喂一份覆盖
        // 全部阶段/事件/网络状态的报告，断言面板发射的 kind 与常量表完全一致。
        var stats = new NmCycleStats(500, 500, 500, 500, 0, 0, 2, 500, 100);
        var report = new NmReport(
            new NmWindow(0, 10, "full"), "src", null,
            new[]
            {
                new NmEpisode(0, 0, 3, "active", null, NmParticipants, true, false),
                new NmEpisode(1, 7, 10, "active", null, NmParticipants, false, true),
            },
            new[]
            {
                new NmMessageReport("0x500", false, "NM", "A", 4, 0, 10, stats,
                    new[]
                    {
                        new NmSegment(0, 1, "wakeup"),
                        new NmSegment(1, 6, "active"),
                        new NmSegment(6, 8, "released"),
                        new NmSegment(8, 10, "silent"),
                        // NMSts 实测切段（spec §7.5.1）：这些 kind 也由面板发射，
                        // 必须一并进常量表，否则会被 UnknownBrush 画成品红。
                        new NmSegment(10, 11, "repeat"),
                        new NmSegment(11, 12, "normal"),
                        new NmSegment(12, 13, "immediate"),
                        new NmSegment(13, 14, "unknown"),
                    }),
                // 原因变化标记只落在匹配的报文行上；给 0x18FFFAF4 一行让 reason 标记可达。
                new NmMessageReport("0x18FFFAF4", true, "BMS_NWM", "BMS", 1, 0, 10, stats,
                    new[] { new NmSegment(0, 10, "active") }),
            },
            Array.Empty<NmNodeReport>(),
            new[]
            {
                new NmEvent(0.5, "burst_observed", "A", "0x500", "3 帧短间隔", 0, "measured"),
                new NmEvent(1.5, "missed_frame", null, "0x500", "缺 1 帧", 0, "inferred"),
                new NmEvent(2.5, "sender_silent", null, "0x500", "2.00s", 0, "inferred"),
                new NmEvent(4.0, "byte_change", null, "0x500", "byte0 变化", 0, "measured"),
                new NmEvent(5.0, "bit_change", null, "0x500", "bit3 变化", 0, "measured"),
                new NmEvent(6.0, "signal_change", null, "0x500", "sig 变化", 0, "measured"),
                new NmEvent(1.0, "bus_wide_silence", null, null, "1.00s", 0, "measured"),
                new NmEvent(3.0, "all_nm_silent", null, null, "NM silent until 7.000s", 0, "measured"),
            },
            Array.Empty<NmFinding>(),
            Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<NmCapabilityState>(), Array.Empty<NmInputMissing>(),
            Array.Empty<NmTruncation>());
        // 面板现在也发射原因段与静默相位：常量表新增 kind 后，这里必须真的产生。
        report = report with
        {
            ReasonTimelines = ReasonTimelinesWithTransition(),
            SilencePhases = new[]
            {
                new NmSilencePhase(3, 4, NmSilencePhaseKind.ReleaseWait,
                    NmSilenceBasis.NmSenderStillActive, 0, 0, 0, 0, null,
                    NmStsEvidence.NotAvailable, false, 0, "detail"),
                new NmSilencePhase(4, 5, NmSilencePhaseKind.PrepareSleep,
                    NmSilenceBasis.NmSenderStoppedAfterDrain, 0, 0, 0, 0, null,
                    NmStsEvidence.NotAvailable, false, 0, "detail"),
                // 留 [6.5,7] 不被相位覆盖，保证残余通用静默带 kind（silence）仍被发射。
                new NmSilencePhase(5, 6, NmSilencePhaseKind.Sleep,
                    NmSilenceBasis.NmSenderStoppedAfterDrain, 0, 0, 0, 0, null,
                    NmStsEvidence.NotAvailable, false, 0, "detail"),
                new NmSilencePhase(6, 6.5, NmSilencePhaseKind.Unknown,
                    NmSilenceBasis.WindowTooShort, 0, 0, 0, 0, null,
                    NmStsEvidence.NotAvailable, false, 0, "detail"),
            },
        };

        var emitted = TraceViewerViewModel.BuildTimelineRows(report)
            .SelectMany(r => r.Segments.Select(s => s.Kind)
                .Concat(r.Markers.Select(m => m.Kind)))
            .Distinct()
            .ToArray();

        // 原因段按取值着色（reason:N / reason_uncertain:N）：动态 kind 不在常量表里，
        // 但必须映射到显式色板，不能落回未知色。
        var dynamicReason = emitted
            .Where(k => NmTimelineKinds.IsReasonKind(k) && k != NmTimelineKinds.Reason)
            .ToArray();
        dynamicReason.Should().NotBeEmpty("按取值着色的原因段必须发射 reason:N");
        dynamicReason.Should().OnlyContain(k =>
            NmSegmentBrushConverter.BrushFor(k) != NmSegmentBrushConverter.UnknownBrush);

        emitted.Except(dynamicReason).Should().BeEquivalentTo(NmTimelineKinds.All,
            "面板发射的每个静态 kind 都必须进常量表，才能被转换器/图例覆盖");
    }

    [Fact]
    public void BuildNetworkTimelineRow_SilencePhases_EmitThreeKinds()
    {
        var report = MakeNetworkReport(
            window: new NmWindow(0, 20, "full"),
            phases: new[]
            {
                Phase(10, 12, NmSilencePhaseKind.ReleaseWait, NmSilenceBasis.NmSenderStillActive),
                Phase(12, 14, NmSilencePhaseKind.PrepareSleep, NmSilenceBasis.NmSenderStoppedAfterDrain),
                Phase(14, 16, NmSilencePhaseKind.Sleep, NmSilenceBasis.NmSenderStoppedAfterDrain),
            });

        var row = TraceViewerViewModel.BuildTimelineRows(report).Single(r => r.Label == "整网状态");

        row.Segments.Select(s => s.Kind).Should().Contain(new[]
        {
            NmTimelineKinds.SilenceReleaseWait,
            NmTimelineKinds.SilencePrepareSleep,
            NmTimelineKinds.SilenceBusSleep,
        });
        row.Segments.Should().OnlyContain(s => s.Evidence == "inferred");
    }

    [Fact]
    public void BuildNetworkTimelineRow_PhasesReplaceGenericSilenceBand()
    {
        var report = MakeNetworkReport(
            window: new NmWindow(0, 20, "full"),
            events: new[] { new NmEvent(10, "all_nm_silent", null, null, "x", 0, "measured") },
            phases: new[]
            {
                Phase(10, 12, NmSilencePhaseKind.ReleaseWait, NmSilenceBasis.NmSenderStillActive),
            });

        var row = TraceViewerViewModel.BuildTimelineRows(report).Single(r => r.Label == "整网状态");

        row.Segments.Should().ContainSingle(s => s.Kind == NmTimelineKinds.SilenceReleaseWait);
        row.Segments.Should().NotContain(s => s.Kind == NmTimelineKinds.Silence
            && s.Start < 12 && s.End > 10,
            "相位覆盖过的区间不能再叠一条灰色带");
    }

    [Fact]
    public void BuildNetworkTimelineRow_DoesNotMergeAcrossKinds()
    {
        var report = MakeNetworkReport(
            window: new NmWindow(0, 20, "full"),
            phases: new[]
            {
                Phase(10, 12, NmSilencePhaseKind.ReleaseWait, NmSilenceBasis.NmSenderStillActive),
                Phase(12, 14, NmSilencePhaseKind.PrepareSleep, NmSilenceBasis.NmSenderStoppedAfterDrain),
            });

        var row = TraceViewerViewModel.BuildTimelineRows(report).Single(r => r.Label == "整网状态");
        var phaseSegments = row.Segments
            .Where(s => NmTimelineKinds.IsSilencePhaseKind(s.Kind))
            .ToArray();

        phaseSegments.Should().HaveCount(2, "相邻但 kind 不同的相位不能被合并成一条");
        phaseSegments.Select(s => s.Kind).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public void BuildNetworkTimelineRow_PartialCoverage_KeepsUnknownResidual()
    {
        var report = MakeNetworkReport(
            window: new NmWindow(0, 20, "full"),
            events: new[] { new NmEvent(10, "all_nm_silent", null, null, "x", 0, "measured") },
            phases: new[]
            {
                Phase(10, 12, NmSilencePhaseKind.ReleaseWait, NmSilenceBasis.NmSenderStillActive),
            });

        var row = TraceViewerViewModel.BuildTimelineRows(report).Single(r => r.Label == "整网状态");

        row.Segments.Should().Contain(s => s.Kind == NmTimelineKinds.Silence && s.Start >= 12);
    }

    [Fact]
    public void BuildNetworkTimelineRow_NoPhases_KeepsLegacySilenceBand()
    {
        var report = MakeNetworkReport(
            window: new NmWindow(0, 20, "full"),
            events: new[] { new NmEvent(10, "all_nm_silent", null, null, "x", 0, "measured") });

        var row = TraceViewerViewModel.BuildTimelineRows(report).Single(r => r.Label == "整网状态");

        row.Segments.Should().Contain(s => s.Kind == NmTimelineKinds.Silence);
    }

    [Fact]
    public void BuildNetworkTimelineRow_TailPhase_MarksTruncated()
    {
        var report = MakeNetworkReport(
            window: new NmWindow(0, 20, "full"),
            phases: new[]
            {
                Phase(18, 20, NmSilencePhaseKind.PrepareSleep,
                    NmSilenceBasis.NmSenderStoppedAfterDrain) with { IsWindowTruncated = true },
            });

        var row = TraceViewerViewModel.BuildTimelineRows(report).Single(r => r.Label == "整网状态");

        row.Segments.Single(s => s.Kind == NmTimelineKinds.SilencePrepareSleep)
            .IsWindowTruncated.Should().BeTrue();
    }

    [Fact]
    public void BuildNetworkTimelineRow_HeadSilenceDrawnFromGapAnalyzerFinding()
    {
        // NmGapAnalyzer 的头部静默 finding 证据是 [窗口起点, 首 episode 起点]；
        // NmAnalyzer 的整窗边界 finding 证据是 [窗口起点, 窗口终点]，必须忽略。
        var report = MakeNetworkReport(
            episodes: new[] { new NmEpisode(0, 4, 9, "active", null, NmParticipants, false, false) },
            findings: new[]
            {
                new NmFinding("NM_TRACE_BOUNDARY", "info", "窗口开头静默", HeadSilenceEvidence,
                    false, null),
                new NmFinding("NM_TRACE_BOUNDARY", "info", "NM 活动触及 trace 边界",
                    WholeWindowBoundaryEvidence, false, null),
            });

        var network = Assert.Single(TraceViewerViewModel.BuildTimelineRows(report));

        var silence = network.Segments.Where(s => s.Kind == "silence").Should().ContainSingle().Which;
        silence.Start.Should().Be(0.0);
        silence.End.Should().Be(4.0);
        silence.Tooltip.Should().Contain("记录边界");
        network.Segments.Should().NotContain(s => s.Kind == "silence" && s.End > 4.0,
            "整窗边界 finding 不能被画成静默带");
    }

    [Fact]
    public void ApplyNmReport_ShowsObservedAndMissingNmMessages()
    {
        var profile = MakeProfile(messages: new[]
        {
            new NmMessageDef("0x500", false, "NM_A", "A", 8, true, CannmReasons),
            new NmMessageDef("0x501", false, "NM_B", "B", 8, true, CannmReasons),
        });
        var report = MakeReport(messages: new[]
        {
            new NmMessageReport("0x500", false, "NM_A", "A", 4, 0, 10,
                new NmCycleStats(500, 500, 500, 500, 0, 0, 1, 500, 100),
                new[] { new NmSegment(0, 10, "active") }),
        });

        var vm = NewVm();
        vm.ApplyNmReport(MakeSnapshot(report, profile));

        vm.NmObservedSummary.Should().Contain("1/2");
        vm.NmObservedSummary.Should().Contain("0x501");
        vm.NmObservedSummary.Should().Contain("NM_B");
    }

    [Fact]
    public void ApplyNmReport_AllMessagesObserved_SaysSo()
    {
        var profile = MakeProfile(messages: new[]
        {
            new NmMessageDef("0x500", false, "NM_A", "A", 8, true, CannmReasons),
        });
        var report = MakeReport(messages: new[]
        {
            new NmMessageReport("0x500", false, "NM_A", "A", 4, 0, 10,
                new NmCycleStats(500, 500, 500, 500, 0, 0, 1, 500, 100),
                new[] { new NmSegment(0, 10, "active") }),
        });

        var vm = NewVm();
        vm.ApplyNmReport(MakeSnapshot(report, profile));

        vm.NmObservedSummary.Should().Contain("1/1");
        vm.NmObservedSummary.Should().Contain("全部出现");
    }

    [Fact]
    public void ApplyNmReport_SetsTimeAxisLabelsFromWindow()
    {
        var vm = NewVm();
        vm.ApplyNmReport(MakeSnapshot(MakeReport(messages: new[]
        {
            new NmMessageReport("0x500", false, "NM", "A", 4, 0, 10,
                new NmCycleStats(500, 500, 500, 500, 0, 0, 1, 500, 100),
                new[] { new NmSegment(0, 10, "active") }),
        }), MakeProfile()));

        vm.NmTimeAxisStart.Should().Be("0.00s");
        vm.NmTimeAxisMid.Should().Be("5.00s");
        vm.NmTimeAxisEnd.Should().Be("10.00s");
    }

    [Fact]
    public void EventRows_ExposeChineseTypeLabel()
    {
        var report = MakeReport(events: new[]
        {
            new NmEvent(1.0, "burst_observed", "A", "0x500", "3 帧短间隔", 0, "measured"),
        });
        var vm = NewVm();
        vm.ApplyNmReport(MakeSnapshot(report, MakeProfile()));

        var row = Assert.Single(vm.NmEventRows);
        row.Type.Should().Be("burst_observed", "筛选契约仍用原始 snake_case");
        row.TypeLabel.Should().Be("突发");
        row.EvidenceLabel.Should().Be("实测");
    }

    [Fact]
    public void SetNmMarkerHover_WritesSharedCardFields()
    {
        var vm = NewVm();

        vm.SetNmMarkerHover("burst", "0x500 NM (A)", 3.2, "突发", "3 帧短间隔");

        // 事件标记与活动段共用同一张信息卡：类型+行、时刻、详情各就各位。
        vm.NmHoverKind.Should().Be("burst");
        vm.NmHoverTitle.Should().Be("突发 · 0x500 NM (A)");
        vm.NmHoverRange.Should().Be("3.20s");
        vm.NmHoverHint.Should().Be("3 帧短间隔");
    }

    [Fact]
    public void SetNmMarkerHover_WithoutRowLabelOrDetail_FallsBackToTypeAndJumpHint()
    {
        var vm = NewVm();

        vm.SetNmMarkerHover("bus_gap", "", 1.0, "总线无帧", "");

        vm.NmHoverTitle.Should().Be("总线无帧");
        vm.NmHoverHint.Should().Contain("点击把绿色锚点移到");
    }

    [Theory]
    [InlineData("measured", "测")]
    [InlineData("inferred", "推")]
    [InlineData("boundary", "边")]
    [InlineData("unexpected", "?")]
    public void NmEventRow_EvidenceGlyph_IsSingleReadableChar(string kind, string glyph)
    {
        // 证据列并入详情列后只剩单字图标；完整级别仍由 ToolTip 提供。
        var row = new NmEventRowVm(1, "1.000", "e", "s", "d", kind, 0);

        row.EvidenceGlyph.Should().Be(glyph);
    }

    [Theory]
    [InlineData("burst_observed", "突发")]
    [InlineData("missed_frame", "漏帧")]
    [InlineData("sender_silent", "发送静默")]
    [InlineData("byte_change", "字节变化")]
    [InlineData("bit_change", "位变化")]
    [InlineData("signal_change", "信号变化")]
    [InlineData("all_nm_silent", "全网 NM 静默")]
    [InlineData("bus_wide_silence", "总线无帧")]
    public void EventTypeLabel_IsChinese(string type, string expected)
        => NmTimelineText.EventType(type).Should().Be(expected);

    [Theory]
    [InlineData("measured", "实测")]
    [InlineData("inferred", "推断")]
    [InlineData("boundary", "边界")]
    public void EvidenceKindLabel_IsChinese(string kind, string expected)
        => NmTimelineText.EvidenceKind(kind).Should().Be(expected);

    [Fact]
    public void SetNmHoverDesignX_MapsDesignCoordinateToWindowTime()
    {
        var vm = NewVm();
        vm.ApplyNmReport(MakeSnapshot(MakeReport(), MakeProfile()));

        vm.SetNmHoverDesignX(0);
        vm.NmHoverTimeText.Should().Be("0.00s");
        vm.SetNmHoverDesignX(500);
        vm.NmHoverTimeText.Should().Be("5.00s");
        vm.SetNmHoverDesignX(1000);
        vm.NmHoverTimeText.Should().Be("10.00s");
        vm.NmHoverActive.Should().BeTrue();

        vm.ClearNmHoverTime();
        vm.NmHoverActive.Should().BeFalse();
        vm.NmHoverTimeText.Should().BeEmpty();

        // 非零窗口起点：中间位置是 15s，不是 5s。
        vm.ApplyNmReport(MakeSnapshot(
            MakeNetworkReport(window: new NmWindow(10, 20, "viewport")), MakeProfile()));
        vm.SetNmHoverDesignX(500);
        vm.NmHoverTimeText.Should().Be("15.00s");
        vm.SetNmHoverDesignX(-100);
        vm.NmHoverTimeText.Should().Be("10.00s", "准星位置必须钳制在窗口范围内");
    }

    [Fact]
    public void SetNmHoverDesignX_WithoutReport_DoesNotFabricateTime()
    {
        var vm = NewVm();

        vm.SetNmHoverDesignX(500);

        vm.NmHoverActive.Should().BeFalse("没有报告窗口时不能显示凭空的准星时间");
        vm.NmHoverTimeText.Should().BeEmpty();
    }

    [Fact]
    public void SetNmSegmentHover_BuildsChineseCardText()
    {
        var vm = NewVm();

        vm.SetNmSegmentHover("active", "0x500 NM (A)", 1.2, 2.4);

        vm.NmHoverKind.Should().Be("active");
        vm.NmHoverTitle.Should().Be("发送中 · 0x500 NM (A)");
        vm.NmHoverRange.Should().Be("1.20s – 2.40s · 持续 1.20s");
        vm.NmHoverHint.Should().Be("点击把绿色锚点移到 1.20s");

        vm.ClearNmSegmentHover();
        vm.NmHoverTitle.Should().BeEmpty();
        vm.NmHoverRange.Should().BeEmpty();
        vm.NmHoverHint.Should().BeEmpty();
    }

    [Fact]
    public void SemanticsBanner_ListsOnlyUnavailableCapabilities()
    {
        var report = MakeReport(capabilities: new[]
        {
            new NmCapabilityState(NmCapability.NidCheck, false,
                new[] { new NmInputMissing(NmCapability.NidCheck, "nid_byte", "why", null, null) }),
            new NmCapabilityState(NmCapability.CbvDecode, true, Array.Empty<NmInputMissing>()),
            new NmCapabilityState(NmCapability.PnCheck, true, Array.Empty<NmInputMissing>()),
            new NmCapabilityState(NmCapability.UserData, true, Array.Empty<NmInputMissing>()),
        });

        var banner = TraceViewerViewModel.BuildSemanticsBanner(report);

        banner.Should().Contain("NID");
        banner.Should().NotContain("CBV");
    }

    [Fact]
    public void SemanticsBanner_HiddenWhenAllSemanticCapabilitiesAvailable()
    {
        var report = MakeReport(capabilities: new[]
        {
            new NmCapabilityState(NmCapability.NidCheck, true, Array.Empty<NmInputMissing>()),
            new NmCapabilityState(NmCapability.CbvDecode, true, Array.Empty<NmInputMissing>()),
            new NmCapabilityState(NmCapability.PnCheck, true, Array.Empty<NmInputMissing>()),
            new NmCapabilityState(NmCapability.UserData, true, Array.Empty<NmInputMissing>()),
        });

        TraceViewerViewModel.BuildSemanticsBanner(report).Should().BeEmpty();
    }

    [Fact]
    public void PartialStatus_DistinguishesCancelFromTimeout()
    {
        var snapshot = MakeSnapshot(MakeReport(isPartial: true), MakeProfile());

        TraceViewerViewModel.ResolveStatus(snapshot, cancelledByUser: true)
            .Should().Contain("取消");
        TraceViewerViewModel.ResolveStatus(snapshot, cancelledByUser: false)
            .Should().Contain("超时");
    }

    private static NmReasonTimeline ReasonTimelineForTest() => new(
        "BMS_Wakeup_reasons", "0x18FFFAF4", true,
        NmReasonKind.Raw, NmReasonKind.Raw,
        [new NmReasonSegment(0.2, 0.2, 0x30,
            [new NmReasonLabel("Waked up by NW PDU", "dbc")],
            // WindowEnd 必须 EndUnknown=true（与 NmReasonAnalyzer 的产出契约一致）。
            false, true, NmReasonSegmentEnd.WindowEnd, null, [1, 0, 0x30])],
        [], [], []);

    [Fact]
    public void BuildTimelineRows_AppendsReasonRowsBeforeNetworkRow()
    {
        var report = MakeReport(
                messages:
                [
                    new NmMessageReport("0x500", false, "NM", "A", 1, 0, 1,
                        new NmCycleStats(500, 500, 500, 500, 0, 0, 1, 500, 100),
                        [new NmSegment(0, 1, "active")]),
                ]) with
        {
            ReasonTimelines = [ReasonTimelineForTest()],
            Episodes = [new NmEpisode(0, 0, 10, "active", null, ["0x500"], false, false)],
        };

        var rows = TraceViewerViewModel.BuildTimelineRows(report);

        var reasonIndex = rows.ToList().FindIndex(
            r => r.Label.Contains("原因 BMS_Wakeup_reasons", StringComparison.Ordinal));
        reasonIndex.Should().BeGreaterThan(0, "原因行在报文行之后");
        rows[^1].Label.Should().Be("整网状态");
        reasonIndex.Should().BeLessThan(rows.Count - 1, "原因行在网络行之前");
    }

    [Fact]
    public void BuildTimelineRows_UnreadableFieldRow_IsLabeled()
    {
        var report = MakeReport() with
        {
            ReasonTimelines = [ReasonTimelineForTest() with
            {
                Segments = [],
                UnreadableFrameTimes = [0.5],
            }],
        };

        var row = Assert.Single(
            TraceViewerViewModel.BuildTimelineRows(report),
            r => r.Label.StartsWith("原因 ", StringComparison.Ordinal));
        row.Label.Should().Contain("字段不可读",
            "DLC 全程不足时，行标签必须明确说明，而不是一条空行");
        row.Segments.Should().BeEmpty();
    }

    [Fact]
    public void BuildTimelineRows_UnresolvedFieldRow_ShowsUnknown()
    {
        var report = MakeReport() with
        {
            ReasonTimelines = [ReasonTimelineForTest() with
            {
                Segments = [],
                Unknowns = ["BMS_Wakeup_reasons：DBC 中未找到该信号（0x18FFFAF4）"],
            }],
        };

        var row = Assert.Single(
            TraceViewerViewModel.BuildTimelineRows(report),
            r => r.Label.StartsWith("原因 ", StringComparison.Ordinal));
        row.Label.Should().Contain("字段不可用")
            .And.Contain("未找到该信号",
                "配置错误（DBC 里没有这个信号）与 DLC 不可读不是一回事，必须显示 Unknowns");
    }

    [Fact]
    public void BuildReasonTimelineRows_SingleFrameSegment_SaysOneFrame()
    {
        var report = MakeReport() with
        {
            ReasonTimelines =
            [
                ReasonTimelineForTest() with
                {
                    Segments =
                    [
                        new NmReasonSegment(0.2, 0.2, 0x30, [], false, false,
                            NmReasonSegmentEnd.Changed, 0.6, [1, 0, 0x30]),
                    ],
                },
            ],
        };

        var row = Assert.Single(TraceViewerViewModel.BuildReasonTimelineRows(report));
        row.Segments
            .Should().ContainSingle(s => s.Kind.StartsWith(NmTimelineKinds.ReasonValuePrefix, StringComparison.Ordinal))
            .Which.DurationText.Should().Be("仅 1 帧");
    }

    [Fact]
    public void BuildReasonTimelineRows_DifferentRawValues_GetDistinctKinds_SameValueReusesKind()
    {
        var report = MakeReport() with
        {
            ReasonTimelines = [ReasonTimelineForTest() with { Segments = ReasonSegmentsWithGaps() }],
        };

        var row = Assert.Single(TraceViewerViewModel.BuildReasonTimelineRows(report));
        var confirmed = row.Segments
            .Where(s => s.Kind.StartsWith(NmTimelineKinds.ReasonValuePrefix, StringComparison.Ordinal))
            .ToArray();

        // 不同原始值必须能看出区别：整行同色就是用户看到的「一坨」。
        confirmed.Should().HaveCount(3);
        confirmed[0].Kind.Should().NotBe(confirmed[1].Kind,
            "0x00 与 0x30 是两个值，必须用不同颜色");
        confirmed[1].Kind.Should().Be(confirmed[2].Kind,
            "同一个原始值再次出现必须复用颜色，否则无从比较");
    }

    [Fact]
    public void BuildReasonTimelineRows_UnobservedGap_IsUncertainBandNotConfirmedValue()
    {
        var report = MakeReport() with
        {
            ReasonTimelines = [ReasonTimelineForTest() with { Segments = ReasonSegmentsWithGaps() }],
        };

        var row = Assert.Single(TraceViewerViewModel.BuildReasonTimelineRows(report));
        var confirmed = row.Segments
            .Where(s => s.Kind.StartsWith(NmTimelineKinds.ReasonValuePrefix, StringComparison.Ordinal))
            .ToArray();
        var uncertain = row.Segments
            .Where(s => s.Kind.StartsWith(NmTimelineKinds.ReasonUncertainPrefix, StringComparison.Ordinal))
            .ToArray();

        // 确认色块只画到最后一个确认帧，不得把未观测区间画成同一个值仍在。
        confirmed.Select(s => (s.Start, s.End)).Should().Equal(
            (0.1, 0.2), (0.6, 4.0), (5.0, 9.0));

        // 未观测区间单独成段，色相可辨（数据模型给出区间边界，不精确到时刻）。
        uncertain.Select(s => (s.Start, s.End)).Should().Equal(
            (0.2, 0.6), (4.0, 5.0));
        uncertain[0].Kind.Should().NotBe(confirmed[0].Kind,
            "未观测区间不能被画成确认值");
    }

    private static NmReasonSegment[] ReasonSegmentsWithGaps() =>
    [
        new NmReasonSegment(0.1, 0.2, 0x00, [], false, false,
            NmReasonSegmentEnd.Changed, 0.6, [1, 0, 0x00]),
        new NmReasonSegment(0.6, 4.0, 0x30, [], false, false,
            NmReasonSegmentEnd.EpisodeEnd, 5.0, [1, 0, 0x30]),
        new NmReasonSegment(5.0, 9.0, 0x30, [], false, true,
            NmReasonSegmentEnd.WindowEnd, null, [1, 0, 0x30]),
    ];

    [Fact]
    public void BuildReasonTimelineRows_EpisodeEndSameValue_DoesNotClaimChange()
    {
        // 真实数据（BMS_Wakeup_reasons 0x30 段 [0.609, 9.714]）：跨 episode 后下一段
        // 仍是同一个值，且没有 reason_change。文案不能说“清除/值变化”。
        var report = MakeReport() with
        {
            ReasonTimelines = [ReasonTimelineForTest() with
            {
                Segments =
                [
                    new NmReasonSegment(0.6, 4.0, 0x30, [], false, false,
                        NmReasonSegmentEnd.EpisodeEnd, 5.0, [1, 0, 0x30]),
                    new NmReasonSegment(5.0, 9.0, 0x30, [], false, true,
                        NmReasonSegmentEnd.WindowEnd, null, [1, 0, 0x30]),
                ],
            }],
        };

        var row = Assert.Single(TraceViewerViewModel.BuildReasonTimelineRows(report));
        var confirmed = row.Segments.Single(s => s.Start == 0.6);
        confirmed.Tooltip.Should().Contain("仍为同一值")
            .And.NotContain("清除发生区间",
                "下一段仍是同一原始值，声称“清除”会与事件表无 reason_change 自相矛盾");

        var uncertain = row.Segments.Single(s =>
            s.Kind.StartsWith(NmTimelineKinds.ReasonUncertainPrefix, StringComparison.Ordinal));
        uncertain.Tooltip.Should().Contain("无法确认是否")
            .And.NotContain("值变化发生在");
    }

    [Fact]
    public void BuildReasonTimelineRows_EpisodeEndDifferentValue_ReportsClearInterval()
    {
        var report = MakeReport() with
        {
            ReasonTimelines = [ReasonTimelineForTest() with
            {
                Segments =
                [
                    new NmReasonSegment(0.6, 4.0, 0x30, [], false, false,
                        NmReasonSegmentEnd.EpisodeEnd, 5.0, [1, 0, 0x30]),
                    new NmReasonSegment(5.0, 9.0, 0x01, [], false, true,
                        NmReasonSegmentEnd.WindowEnd, null, [1, 0, 0x01]),
                ],
            }],
        };

        var row = Assert.Single(TraceViewerViewModel.BuildReasonTimelineRows(report));
        var confirmed = row.Segments.Single(s => s.Start == 0.6);
        confirmed.Tooltip.Should().Contain("清除发生区间 4.000s – 5.000s");
    }

    [Fact]
    public void BuildReasonTimelineRows_DurationWording_MatchesConfidence()
    {
        var report = MakeReport() with
        {
            ReasonTimelines = [ReasonTimelineForTest() with
            {
                Segments =
                [
                    // 起点未知（首帧前无观测）：只能是下界。
                    new NmReasonSegment(0.2, 0.4, 0x01, [], true, false,
                        NmReasonSegmentEnd.Changed, 1.0, [1, 0, 0x01]),
                    // 起点有界且 Changed：可以说“观测区间”。
                    new NmReasonSegment(1.0, 3.0, 0x02, [], false, false,
                        NmReasonSegmentEnd.Changed, 4.0, [1, 0, 0x02]),
                    // EpisodeEnd：终点不是值变化帧，只能下界。
                    new NmReasonSegment(4.0, 6.0, 0x03, [], false, true,
                        NmReasonSegmentEnd.WindowEnd, null, [1, 0, 0x03]),
                ],
            }],
        };

        var row = Assert.Single(TraceViewerViewModel.BuildReasonTimelineRows(report));
        var confirmed = row.Segments
            .Where(s => s.Kind.StartsWith(NmTimelineKinds.ReasonValuePrefix, StringComparison.Ordinal))
            .ToArray();

        confirmed[0].DurationText.Should().Be("≥ 0.200s");
        confirmed[1].DurationText.Should().Be("观测区间 2.000s");
        confirmed[2].DurationText.Should().Be("≥ 2.000s");
    }

    [Fact]
    public void BuildReasonTimelineRows_UnreadableRecoveryWithDifferentValue_SaysChangeTimeUnknown()
    {
        // 不可读后恢复为不同值：分析器确实产了 transition，文案不能说“无法确认是否变化”。
        var report = MakeReport() with
        {
            ReasonTimelines = [ReasonTimelineForTest() with
            {
                Segments =
                [
                    new NmReasonSegment(0.1, 0.1, 0x00, [], false, false,
                        NmReasonSegmentEnd.Unreadable, 1.6, [1, 0, 0x00]),
                    new NmReasonSegment(1.6, 2.0, 0x30, [], false, true,
                        NmReasonSegmentEnd.WindowEnd, null, [1, 0, 0x30]),
                ],
            }],
        };

        var row = Assert.Single(TraceViewerViewModel.BuildReasonTimelineRows(report));
        var confirmed = row.Segments.Single(s =>
            s.Start == 0.1 && s.Kind.StartsWith(NmTimelineKinds.ReasonValuePrefix, StringComparison.Ordinal));
        confirmed.Tooltip.Should().Contain("恢复后值已不同")
            .And.NotContain("无法确认是否变化");

        var uncertain = row.Segments.Single(s =>
            s.Kind.StartsWith(NmTimelineKinds.ReasonUncertainPrefix, StringComparison.Ordinal));
        uncertain.Tooltip.Should().Contain("恢复后值已不同")
            .And.NotContain("无法确认是否变化");
    }

    [Fact]
    public void BuildReasonTimelineRows_TruncatedSegments_DoesNotClaimValueChanged()
    {
        // 段数超过 200 时下一段被丢弃：无法比较下一段取值，不得断言“清除/变化”。
        var report = MakeReport() with
        {
            ReasonTimelines = [ReasonTimelineForTest() with
            {
                Segments =
                [
                    new NmReasonSegment(0.6, 4.0, 0x30, [], false, false,
                        NmReasonSegmentEnd.EpisodeEnd, 5.0, [1, 0, 0x30]),
                ],
            }],
            Truncations =
            [
                new NmTruncation("reason:segments:0x18FFFAF4:BMS_Wakeup_reasons", 200, 260),
            ],
        };

        var row = Assert.Single(TraceViewerViewModel.BuildReasonTimelineRows(report));
        row.Label.Should().Contain("截断");
        var confirmed = row.Segments.Single(s =>
            s.Kind.StartsWith(NmTimelineKinds.ReasonValuePrefix, StringComparison.Ordinal));
        confirmed.Tooltip.Should().Contain("超出展示上限")
            .And.NotContain("清除发生区间");

        var uncertain = row.Segments.Single(s =>
            s.Kind.StartsWith(NmTimelineKinds.ReasonUncertainPrefix, StringComparison.Ordinal));
        uncertain.Tooltip.Should().Contain("超出展示上限")
            .And.NotContain("值变化发生在");
    }

    [Fact]
    public void SeekToNmTimeOrIgnore_MovesGreenAnchor()
    {
        // 标定规则行的「触发」也是定位入口，必须走同一条可见路径。
        var vm = NewVm();

        vm.SeekToNmTimeOrIgnoreCommand.Execute(3.5);
        vm.AnchorTimestampSeconds.Should().Be(3.5);
        vm.NmSeekStatus.Should().Contain("3.50s");

        // 无证据时刻（null）不得把锚点拉到 0 秒。
        vm.SeekToNmTimeOrIgnoreCommand.Execute((double?)null);
        vm.AnchorTimestampSeconds.Should().Be(3.5);
    }

    [Fact]
    public void RefreshAtAnchor_ClearsStaleSeekStatus()
    {
        // 用户随后拖绿线/换窗口时，“已把锚点移到 2.50s”必须失效，不能一直挂着旧时刻。
        var vm = NewVm();
        vm.SeekToNmTimeCommand.Execute(2.5);
        vm.NmSeekStatus.Should().Contain("2.50s");

        vm.RefreshAtAnchor(5.0);

        vm.AnchorTimestampSeconds.Should().Be(5.0);
        vm.NmSeekStatus.Should().BeEmpty();
    }

    [Fact]
    public void SeekToNmTime_MovesGreenAnchor_AndShowsStatus()
    {
        // 回放已废除：_masterService.Seek 只改一个没人显示的时间戳。
        // 用户可感知的“定位到某时刻”是绿色锚点（图表竖线 + 监视列表最新值）。
        var vm = NewVm();

        vm.SeekToNmTimeCommand.Execute(2.5);

        vm.AnchorTimestampSeconds.Should().Be(2.5);
        vm.NmSeekStatus.Should().Contain("2.50s").And.Contain("绿色锚点");
    }

    [Fact]
    public void ApplyNmReport_ReasonHint_ShowsPerFieldUnknownsEvenWithSegments()
    {
        var vm = NewVm();
        var report = MakeReport() with
        {
            ReasonTimelines = [ReasonTimelineForTest() with
            {
                Unknowns = ["BMS_Wakeup_reasons：kind=bitmask 未展开，按原始值显示"],
            }],
            ReasonDictionaryPath = "reasons.nmreasons.json",
        };

        vm.ApplyNmReport(MakeSnapshot(report, MakeProfile()));

        // 有段不等于没有降级：位掩码未展开这类字段级说明必须仍然可见。
        vm.NmReasonHint.Should().Contain("bitmask").And.Contain("未展开");
    }

    [Fact]
    public void BuildReasonTimelineRows_WindowEndSegment_IsWindowTruncated()
    {
        var row = Assert.Single(
            TraceViewerViewModel.BuildReasonTimelineRows(
                MakeReport() with { ReasonTimelines = [ReasonTimelineForTest()] }));
        // ReasonTimelineForTest 的段是 WindowEnd：时间条必须显示窗口截断。
        row.Segments.Should().ContainSingle()
            .Which.IsWindowTruncated.Should().BeTrue();
    }

    [Fact]
    public void ApplyNmReport_ReasonHint_UnreadableField_SaysUnreadable()
    {
        var vm = NewVm();
        var report = MakeReport() with
        {
            ReasonTimelines = [ReasonTimelineForTest() with
            {
                Segments = [],
                Unknowns = [],
                UnreadableFrameTimes = [0.5],
            }],
            ReasonDictionaryPath = "reasons.nmreasons.json",
        };

        vm.ApplyNmReport(MakeSnapshot(report, MakeProfile()));

        // 行标签与顶部提示必须说同一件事：DLC 不足 ≠ 窗口内没有帧。
        vm.NmReasonHint.Should().Contain("字段不可读").And.NotContain("没有可观测帧");
    }

    [Fact]
    public void ApplyNmReport_ReasonHint_AllFieldsUnreadable()
    {
        var vm = NewVm();
        var report = MakeReport() with
        {
            ReasonTimelines = [ReasonTimelineForTest() with { Segments = [] }],
            ReasonDictionaryPath = "reasons.nmreasons.json",
        };

        vm.ApplyNmReport(MakeSnapshot(report, MakeProfile()));

        // 有字典、有字段，但一条可用时间线也没有：不能静默。
        vm.NmReasonHint.Should().NotBeEmpty();
    }

    [Fact]
    public void BuildTimelineRows_NoDictionary_HasNoReasonRows()
    {
        Assert.DoesNotContain(TraceViewerViewModel.BuildTimelineRows(MakeReport()),
            r => r.Label.StartsWith("原因 ", StringComparison.Ordinal));
    }

    [Fact]
    public void ReasonTransitions_ProduceEventRows()
    {
        var report = MakeReport(
            events:
            [
                new NmEvent(7.0, "burst_observed", "A", "0x500", "3 帧短间隔", 0, "measured"),
            ]) with
        {
            Episodes =
            [
                new NmEpisode(0, 0, 1, "active", null, ["0x500"], false, false),
                new NmEpisode(1, 5, 9, "active", null, ["0x500"], false, false),
            ],
            ReasonTimelines = ReasonTimelinesWithTransition(),
        };
        var vm = NewVm();
        vm.ApplyNmReport(MakeSnapshot(report, MakeProfile()));

        var row = vm.NmEventRows.Should().ContainSingle(r => r.Type == "reason_change").Subject;
        // 精确断言：Old/New 写反、误用 OldLabels 都必须变红。
        row.Detail.Should().Be("BMS_Wakeup_reasons: 0x0 → 0x30（Waked up by NW PDU）");
        row.TypeLabel.Should().Be("原因变化");
        row.EvidenceKind.Should().Be("measured");
        // transition 在 6.0s，落在第二个 episode：episode 归属必须真的算。
        row.EpisodeIndex.Should().Be(1);
        // 事件表按时间排序：原因变化（6.0s）在突发（7.0s）之前。
        vm.NmEventRows.Select(r => r.T).Should().BeInAscendingOrder();
        vm.NmEventRows[0].Type.Should().Be("reason_change");
    }

    [Fact]
    public void BuildTimelineRows_ReasonTransition_AddsMarkerOnMessageRow()
    {
        var report = MakeReport(
            messages:
            [
                new NmMessageReport("0x18FFFAF4", true, "BMS_NWM", "BMS", 1, 0, 1,
                    new NmCycleStats(500, 500, 500, 500, 0, 0, 1, 500, 100),
                    [new NmSegment(0, 1, "active")]),
            ]) with
        {
            ReasonTimelines = ReasonTimelinesWithTransition(),
        };

        var rows = TraceViewerViewModel.BuildTimelineRows(report);
        var messageRow = rows.Single(r => r.Label.StartsWith("0x18FFFAF4", StringComparison.Ordinal));
        var marker = messageRow.Markers.Should().ContainSingle().Subject;
        marker.Kind.Should().Be(NmTimelineKinds.Reason);
        marker.T.Should().Be(6.0);
        NmEventShapeConverter.ShapeFor(marker.Kind)
            .Should().NotBeSameAs(NmEventShapeConverter.UnknownShape,
                "原因标记必须使用注册形状，不能落到未知六边形");
    }

    [Fact]
    public void ReasonChange_IsVisibleUnderSignalCategory()
    {
        var report = MakeReport() with { ReasonTimelines = ReasonTimelinesWithTransition() };
        var vm = NewVm();
        vm.ApplyNmReport(MakeSnapshot(report, MakeProfile()));

        vm.NmSelectedEventTypeFilter = "信号";

        // 被字典覆盖的信号不再产 signal_change；原因变化必须接替它留在「信号」分类。
        vm.NmEventRows.Should().ContainSingle(r => r.Type == "reason_change");
    }

    private static NmReasonTimeline[] ReasonTimelinesWithTransition() =>
    [
        ReasonTimelineForTest() with
        {
            Transitions =
            [
                new NmReasonTransition(6.0, "BMS_Wakeup_reasons", "0x18FFFAF4", true,
                    0x00, 0x30, [],
                    [new NmReasonLabel("Waked up by NW PDU", "dbc")]),
            ],
        },
    ];

    [Fact]
    public void ApplyNmReport_ReasonHint_DictionaryLoadFailureShowsNote()
    {
        var vm = NewVm();
        var report = MakeReport() with
        {
            Unknowns = ["原因字典不可用：原因字典不存在：C:\\nets\\missing.json"],
        };

        vm.ApplyNmReport(MakeSnapshot(report, MakeProfile()));

        // 配置了但加载失败时不能误报成“未配置”：失败原因必须直接可见。
        vm.NmReasonHint.Should().Contain("原因字典不可用").And.Contain("missing.json");
        vm.NmReasonHint.Should().NotContain("未加载");
    }

    [Fact]
    public void BuildReasonTimelineRows_TooltipIncludesFirstFrameBytes()
    {
        var row = Assert.Single(
            TraceViewerViewModel.BuildReasonTimelineRows(
                MakeReport() with { ReasonTimelines = [ReasonTimelineForTest()] }));

        // spec §8：原因行 tooltip 必须给出该段首帧原始载荷，且逐字节可读
        // （一整串 010030 用户读不懂，要带 byte 序号）。
        row.Segments.Should().ContainSingle()
            .Which.Tooltip.Should().Contain("首帧原始载荷 01 00 30（byte0→byte2）");
    }

    [Fact]
    public void ApplyNmReport_ReasonHint_NoDictionary()
    {
        var vm = NewVm();
        vm.ApplyNmReport(MakeSnapshot(MakeReport(), MakeProfile()));
        vm.NmReasonHint.Should().Contain("reasons_path");
    }

    [Fact]
    public void ApplyNmReport_ReasonHint_DictionaryConfiguredButEmpty()
    {
        var vm = NewVm();
        var report = MakeReport() with { ReasonDictionaryPath = "reasons.nmreasons.json" };
        vm.ApplyNmReport(MakeSnapshot(report, MakeProfile()));
        vm.NmReasonHint.Should().Contain("未产生可用时间线");
    }

    [Fact]
    public void ApplyNmReport_ReasonHint_HiddenWhenTimelinePresent()
    {
        var vm = NewVm();
        var report = MakeReport() with { ReasonTimelines = [ReasonTimelineForTest()] };
        vm.ApplyNmReport(MakeSnapshot(report, MakeProfile()));
        vm.NmReasonHint.Should().BeEmpty();
    }

    private static NmReport MakeReport(
        NmMessageReport[]? messages = null,
        NmEvent[]? events = null,
        NmCapabilityState[]? capabilities = null,
        NmInputMissing[]? missing = null,
        bool isPartial = false)
        => new(
            new NmWindow(0, 10, "full"), "src", null,
            Array.Empty<NmEpisode>(),
            messages ?? Array.Empty<NmMessageReport>(),
            Array.Empty<NmNodeReport>(),
            events ?? Array.Empty<NmEvent>(),
            Array.Empty<NmFinding>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            capabilities ?? Array.Empty<NmCapabilityState>(),
            missing ?? Array.Empty<NmInputMissing>(),
            Array.Empty<NmTruncation>(),
            EngineVersion: NmAnalyzer.EngineVersion,
            IsPartial: isPartial);

    /// <summary>网络行测试用最小报告：只需要 episodes / events / findings。</summary>
    private static NmReport MakeNetworkReport(
        NmEpisode[]? episodes = null,
        NmEvent[]? events = null,
        NmFinding[]? findings = null,
        NmWindow? window = null,
        NmSilencePhase[]? phases = null)
        => new(
            window ?? new NmWindow(0, 10, "full"), "src", null,
            episodes ?? Array.Empty<NmEpisode>(),
            Array.Empty<NmMessageReport>(),
            Array.Empty<NmNodeReport>(),
            events ?? Array.Empty<NmEvent>(),
            findings ?? Array.Empty<NmFinding>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<NmCapabilityState>(),
            Array.Empty<NmInputMissing>(),
            Array.Empty<NmTruncation>(),
            EngineVersion: NmAnalyzer.EngineVersion)
        { SilencePhases = phases ?? Array.Empty<NmSilencePhase>() };

    private static NmSilencePhase Phase(
        double start, double end, NmSilencePhaseKind kind, NmSilenceBasis basis)
        => new(start, end, kind, basis, 0, 0, 0, 0, null,
            NmStsEvidence.NotAvailable, false, 0, "detail");

    private static NmProfile MakeProfile(
        NmProfileSource source = NmProfileSource.Dbc,
        NmMessageDef[]? messages = null,
        NmTimings? timings = null,
        NmNodeDef[]? nodes = null,
        NmConfigConflict[]? conflicts = null,
        string[]? missingFields = null)
        => new(
            source,
            messages ?? Array.Empty<NmMessageDef>(),
            timings ?? new NmTimings(),
            nodes ?? Array.Empty<NmNodeDef>(),
            conflicts ?? Array.Empty<NmConfigConflict>(),
            missingFields ?? Array.Empty<string>(),
            new NmProvenance());

    private static NmToolSnapshot MakeSnapshot(
        NmReport report, NmProfile profile, string? dbcHash = "hash")
        => new("src", null, "C:/nets/net.dbc", dbcHash, profile, report,
            Array.Empty<ReplayFrame>());

    private static TraceViewerViewModel NewVmWithSource(
        DbcDocument? dbc, IReadOnlyList<ReplayFrame> frames)
    {
        var session = Substitute.For<ITraceSessionService>();
        session.WatchedSignals.Returns(new ObservableCollection<WatchedSignalRow>());
        session.SignalGroups.Returns(new ObservableCollection<WatchedSignalGroup>());
        var registry = Substitute.For<ITraceSessionRegistry>();
        var viewer = Substitute.For<ITraceViewerService>();
        viewer.LoadedFrames.Returns(frames);
        registry.Sources.Returns(new List<TraceSource>
        {
            new("src", "t", "C:/t.asc", Colors.Blue, new LineStyle()),
        });
        registry.GetService("src").Returns(viewer);
        var dbcService = new DbcService(NullLogger<DbcService>.Instance);
        if (dbc is not null) dbcService.SetCurrentForTests(dbc);
        var libPath = Path.Combine(Path.GetTempPath(), $"nm-panel-{Guid.NewGuid():N}.tmtrace");
        return new TraceViewerViewModel(session, registry, dbcService,
            NullLogger<TraceViewerViewModel>.Instance,
            new TraceSessionLibrary(libPath, NullLogger<TraceSessionLibrary>.Instance));
    }

    private static TraceViewerViewModel NewVmWithRegistry(ITraceSessionRegistry registry)
    {
        var session = Substitute.For<ITraceSessionService>();
        session.WatchedSignals.Returns(new ObservableCollection<WatchedSignalRow>());
        session.SignalGroups.Returns(new ObservableCollection<WatchedSignalGroup>());
        var dbcService = Substitute.For<DbcService>(NullLogger<DbcService>.Instance);
        var libPath = Path.Combine(Path.GetTempPath(), $"nm-panel-{Guid.NewGuid():N}.tmtrace");
        return new TraceViewerViewModel(session, registry, dbcService,
            NullLogger<TraceViewerViewModel>.Instance,
            new TraceSessionLibrary(libPath, NullLogger<TraceSessionLibrary>.Instance));
    }

    /// <summary>与 GreenLineAnchorFlowTests.NewVm 同形：只为面板测试构造
    /// 最小可用的 TraceViewerViewModel，不新增生产代码路径。</summary>
    private static TraceViewerViewModel NewVm()
    {
        var session = Substitute.For<ITraceSessionService>();
        session.WatchedSignals.Returns(new ObservableCollection<WatchedSignalRow>());
        session.SignalGroups.Returns(new ObservableCollection<WatchedSignalGroup>());
        var registry = Substitute.For<ITraceSessionRegistry>();
        registry.Sources.Returns(new List<TraceSource>());
        var dbcService = Substitute.For<DbcService>(NullLogger<DbcService>.Instance);
        var libPath = Path.Combine(Path.GetTempPath(), $"nm-panel-{Guid.NewGuid():N}.tmtrace");
        return new TraceViewerViewModel(session, registry, dbcService,
            NullLogger<TraceViewerViewModel>.Instance,
            new TraceSessionLibrary(libPath, NullLogger<TraceSessionLibrary>.Instance));
    }
}
