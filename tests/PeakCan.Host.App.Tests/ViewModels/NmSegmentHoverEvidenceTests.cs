using System.Collections.ObjectModel;
using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.ViewModels;
using PeakCan.HIL.Core.Dbc;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// spec §7.5.2.1：hover 文案必须区分证据等级，且「窗口截断」是**后缀拼接**而非替换
/// （两者正交）。
/// </summary>
public class NmSegmentHoverEvidenceTests
{
    /// <summary>hover 只写属性、不碰会话，但 ViewModel 的构造器要求一套依赖。</summary>
    private static TraceViewerViewModel NewVm()
    {
        var session = Substitute.For<ITraceSessionService>();
        session.WatchedSignals.Returns(new ObservableCollection<WatchedSignalRow>());
        session.SignalGroups.Returns(new ObservableCollection<WatchedSignalGroup>());
        var registry = Substitute.For<ITraceSessionRegistry>();
        var libPath = Path.Combine(Path.GetTempPath(), $"nm-hover-{Guid.NewGuid():N}.tmtrace");
        return new TraceViewerViewModel(session, registry,
            new DbcService(NullLogger<DbcService>.Instance),
            NullLogger<TraceViewerViewModel>.Instance,
            new TraceSessionLibrary(libPath, NullLogger<TraceSessionLibrary>.Instance));
    }
    [Fact]
    public void SetNmSegmentHover_KeepsJumpHintSeparateFromEvidence()
    {
        // 证据不得挤占「点击跳转到 X」——两者是不同信息，塞进同一个属性会丢一个。
        var vm = NewVm();

        vm.SetNmSegmentHover("repeat", "0x500 NM (A)", 1.2, 2.4, "measured", false);

        vm.NmHoverHint.Should().Be("点击跳转到 1.20s");
        vm.NmHoverEvidence.Should().Be("实测（NMSts）");
    }

    [Theory]
    [InlineData("measured", "实测（NMSts）")]
    [InlineData("inferred", "推断（T_REPEAT_MESSAGE）")]
    [InlineData("boundary", "边界截断")]
    public void SetNmSegmentHover_ShowsEvidenceLabel(string evidence, string expected)
    {
        var vm = NewVm();

        vm.SetNmSegmentHover("repeat", "0x500 NM (A)", 1.2, 2.4, evidence, false);

        vm.NmHoverEvidence.Should().Be(expected);
    }

    [Fact]
    public void SetNmSegmentHover_TruncatedAppendsSuffixNotReplaces()
    {
        // 「实测（NMSts），窗口截断」——证据与截断是两件事，不能互相覆盖。
        var vm = NewVm();

        vm.SetNmSegmentHover("repeat", "0x500 NM (A)", 1.2, 2.4, "measured", isWindowTruncated: true);

        vm.NmHoverEvidence.Should().Be("实测（NMSts），窗口截断");
    }

    [Fact]
    public void SetNmSegmentHover_UnknownEvidenceFallsBackToItself()
    {
        // 未知证据等级原样显示，不猜（与 NmTimelineText.Provenance 同策略）。
        var vm = NewVm();

        vm.SetNmSegmentHover("repeat", "row", 0, 1, "something_new", false);

        vm.NmHoverEvidence.Should().Be("something_new");
    }

    [Fact]
    public void SetNmSegmentHover_EmptyEvidence_KeepsHintEmpty()
    {
        // 事件标记复用同一张卡时不传证据：不得凭空显示「推断」。
        var vm = NewVm();

        vm.SetNmSegmentHover("repeat", "row", 0, 1, "", false);

        vm.NmHoverEvidence.Should().BeEmpty();
    }

    [Fact]
    public void SetNmMarkerHover_ClearsStaleEvidenceFromPreviousSegment()
    {
        // 复现路径：先停在 repeat 段（卡片显示「实测（NMSts）」），再移到同行的事件标记。
        // View 侧切到标记时先清 _nmHoveredSegment 再调 SetNmMarkerHover，
        // 段离开时的清理被 ReferenceEquals 守卫吃掉——只能在 SetNmMarkerHover 里复位。
        var vm = NewVm();
        vm.SetNmSegmentHover("repeat", "0x500 NM (A)", 0, 1, "measured", false);
        vm.NmHoverEvidence.Should().Be("实测（NMSts）");

        vm.SetNmMarkerHover("burst", "0x500 NM (A)", 0.5, "突发", "3 帧短间隔");

        vm.NmHoverEvidence.Should().BeEmpty("标记没有段证据，不能残留上一段的");
    }

    [Theory]
    [InlineData("repeat")]
    [InlineData("wakeup")]
    [InlineData("silent")]
    [InlineData("unknown")]
    public void SegmentEvidence_Inferred_CitesKindSpecificBasis(string kind)
    {
        // spec §7.5.2.1：推断的依据随 kind 不同。统一写 T_REPEAT_MESSAGE 会让
        // 悬停在 silent 段时声称一个与它无关的依据。
        var text = NmTimelineText.SegmentEvidence("inferred", kind);

        text.Should().StartWith("推断");
        if (kind != "repeat")
            text.Should().NotContain("T_REPEAT_MESSAGE",
                $"kind {kind} 的推断依据不是 T_REPEAT_MESSAGE");
    }

    [Fact]
    public void SegmentEvidence_RepeatInferred_CitesRepeatMessageWindow()
    {
        NmTimelineText.SegmentEvidence("inferred", "repeat")
            .Should().Contain("T_REPEAT_MESSAGE");
    }
}
