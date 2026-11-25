using System.Collections.ObjectModel;
using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.App.Converters;
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
    public void SetNmSegmentHover_PrepareSleep_ShowsDrainAndQuietEvidence()
    {
        var vm = NewVm();

        vm.SetNmSegmentHover(NmTimelineKinds.SilencePrepareSleep, "整网状态",
            12, 14, "inferred", detail: "超时后仅 7 帧清缓存尾巴；0.92s 总线无帧");

        vm.NmHoverTitle.Should().Contain("准备休眠（推断）");
        vm.NmHoverHint.Should().Contain("清缓存");
        vm.NmHoverEvidence.Should().Contain("推断");
        vm.NmHoverEvidence.Should().NotContain("实测");
    }

    [Fact]
    public void SetNmSegmentHover_ReleaseWait_ShowsSenderEvidence()
    {
        var vm = NewVm();

        vm.SetNmSegmentHover(NmTimelineKinds.SilenceReleaseWait, "整网状态",
            10, 12, "inferred", detail: "发送者仍在发送 354 帧应用报文 → 仍处于网络模式");

        vm.NmHoverTitle.Should().Contain("释放等待（推断）");
        vm.NmHoverHint.Should().Contain("354");
        vm.NmHoverEvidence.Should().Contain("发送者仍在发应用报文");
    }

    [Fact]
    public void SetNmSegmentHover_BusSleep_ShowsInferredCaveat()
    {
        var vm = NewVm();

        vm.SetNmSegmentHover(NmTimelineKinds.SilenceBusSleep, "整网状态",
            14, 18, "inferred", detail: "等待窗口内总线无帧");

        vm.NmHoverTitle.Should().Contain("总线睡眠（推断）");
        vm.NmHoverEvidence.Should().Contain("推断");
        vm.NmHoverEvidence.Should().NotContain("实测");
    }

    [Fact]
    public void SetNmSegmentHover_UnknownPhase_ShowsDetailNotTimeoutText()
    {
        var vm = NewVm();

        vm.SetNmSegmentHover(NmTimelineKinds.SilencePhaseUnknown, "整网状态",
            32.536, 32.570, "inferred", detail: "窗口仅 0.034s，短于清缓存宽限 0.500s");

        vm.NmHoverTitle.Should().Contain("无法判定（推断）");
        vm.NmHoverHint.Should().Contain("短于清缓存宽限");
        vm.NmHoverEvidence.Should().Contain("窗口不足");
        vm.NmHoverEvidence.Should().NotContain("超时未达静默阈值");
    }

    [Fact]
    public void SetNmSegmentHover_KeepsJumpHintSeparateFromEvidence()
    {
        // 证据不得挤占「点击定位到 X」——两者是不同信息，塞进同一个属性会丢一个。
        var vm = NewVm();

        vm.SetNmSegmentHover("repeat", "0x500 NM (A)", 1.2, 2.4, "measured", false);

        vm.NmHoverHint.Should().Be("点击把绿色锚点移到 1.20s");
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
    public void SetNmSegmentHover_Reason_UsesRowLabelObservedDurationAndRawEvidence()
    {
        // 原因段的可见 hover 卡片必须与数据模型一致：
        // 标题不重复「原因」前缀；时长用观测下界；证据是原始值而不是 NMSts。
        var vm = NewVm();

        vm.SetNmSegmentHover(
            NmTimelineKinds.Reason, "原因 BMS_Wakeup_reasons",
            0.609, 9.714, "measured", isWindowTruncated: false,
            durationText: "≥ 9.105s",
            detail: "Waked up by NW PDU；原始值 0x30；来源 dbc；" +
                    "清除发生区间 9.714s – 12.677s（相邻观测，非精确时刻）");

        vm.NmHoverTitle.Should().Be("原因 BMS_Wakeup_reasons");
        vm.NmHoverRange.Should().StartWith("0.61s – 9.71s").And.Contain("≥ 9.105s");
        vm.NmHoverEvidence.Should().Be("实测（原始值）");
        vm.NmHoverHint.Should().Contain("Waked up by NW PDU")
            .And.Contain("0x30")
            .And.Contain("点击把绿色锚点移到 0.61s");
    }

    [Fact]
    public void SetNmSegmentHover_UncertainBand_SaysUnobservedNotMeasured()
    {
        // 未观测带本身就不是“实测值”：证据词、标题、时长模板都必须跟着变，
        // 否则会出现「实测（原始值）」与「无法判断」同卡并列。
        var vm = NewVm();

        vm.SetNmSegmentHover(
            NmTimelineKinds.ReasonUncertain(0), "原因 BMS_Wakeup_reasons",
            9.714, 12.677, "unobserved", isWindowTruncated: false,
            durationText: "无法确认是否变化",
            detail: "未观测区间 9.714s – 12.677s：无法确认是否发生变化");

        vm.NmHoverTitle.Should().Contain("未观测区间");
        vm.NmHoverRange.Should().NotContain("持续 无法确认");
        vm.NmHoverEvidence.Should().Be("未观测（区间内无法确认）");
        vm.NmHoverHint.Should().Contain("无法确认是否发生变化");
    }

    [Fact]
    public void SetNmSegmentHover_ReasonValueKind_UsesReasonCard()
    {
        // 原因段改制为按值着色后，kind 不再是裸 "reason"；hover 分支必须跟着走，
        // 否则可见卡片会退化成通用段卡片（标题重复、证据写成 NMSts）。
        var vm = NewVm();

        vm.SetNmSegmentHover(
            NmTimelineKinds.ReasonValue(1), "原因 BMS_Wakeup_reasons",
            0.6, 4.0, "measured", isWindowTruncated: false,
            durationText: "≥ 3.400s", detail: "原始值 0x30");

        vm.NmHoverTitle.Should().Be("原因 BMS_Wakeup_reasons");
        vm.NmHoverEvidence.Should().Be("实测（原始值）");
        vm.NmHoverHint.Should().Contain("原始值 0x30");
    }

    [Fact]
    public void SetNmSegmentHover_ReasonTruncated_AppendsSuffix()
    {
        var vm = NewVm();

        vm.SetNmSegmentHover(
            NmTimelineKinds.Reason, "原因 S", 1.0, 1.0, "measured",
            isWindowTruncated: true, durationText: "仅 1 帧", detail: "含义未配置（0x30）");

        vm.NmHoverEvidence.Should().Be("实测（原始值），窗口截断");
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
