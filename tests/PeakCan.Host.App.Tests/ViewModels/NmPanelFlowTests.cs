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
                    new[] { new NmSegment(2, 8, "active") }, false),
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
                }, false),
        });

        var row = Assert.Single(TraceViewerViewModel.BuildTimelineRows(report));

        // 每报文一行（spec §9.1），Kind 原样保留给着色转换器。
        row.Segments.Select(s => s.Kind).Should().Equal("wakeup", "active", "released", "silent");
        row.Segments[0].Tooltip.Should().Contain("wakeup");
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
        vm.NmProfileSummary.Should().Contain("识别依据 MsgType=CANNM ×2 / 用户勾选 ×1");
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
        // 直接应用 partial 报告时无法得知是取消还是超时；具体原因由 ResolveStatus 用例覆盖。
        vm.NmStatus.Should().Contain("部分结果");
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
        var key = new NmCacheKey("src", null, 10, "hash", "supp", 0, 10, NmAnalyzer.EngineVersion, "full");

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
                    new[] { new NmSegment(0, 10, "active") }, false),
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
        var messageKinds = rows[0].Segments.Select(s => s.Kind).ToArray();
        messageKinds.Should().Contain("burst");
        messageKinds.Should().Contain("byte");
        messageKinds.Should().Contain("gap");
        // 网络行：episode 活动段 + 4s–7s 静默段 + 两个边界标记。
        rows[1].Label.Should().Contain("网络");
        var networkKinds = rows[1].Segments.Select(s => s.Kind).ToArray();
        networkKinds.Should().Contain("episode");
        networkKinds.Should().Contain("silence");
        networkKinds.Should().Contain("boundary");
        var silence = rows[1].Segments.Single(s => s.Kind == "silence");
        silence.Start.Should().Be(4.0);
        silence.End.Should().Be(7.0);
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
