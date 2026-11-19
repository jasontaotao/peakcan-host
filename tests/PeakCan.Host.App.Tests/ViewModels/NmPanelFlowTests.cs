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

public class NmPanelFlowTests
{
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
