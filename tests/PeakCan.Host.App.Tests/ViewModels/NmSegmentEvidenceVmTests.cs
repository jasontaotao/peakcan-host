using FluentAssertions;
using PeakCan.Host.App.Converters;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core.Analysis.Nm;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// spec §7.5.2.1：证据字段必须走完整数据通路
/// （<see cref="NmSegment"/> → <see cref="NmTimelineSegmentVm"/> → hover 文案）。
/// <para>
/// 只改 hover 签名不够：VM 不带字段，hover 拿到的永远是默认值，
/// 「实测/推断」的区分落不了地，而测试只断言文案时看不出这一点。
/// </para>
/// </summary>
public class NmSegmentEvidenceVmTests
{
    private static NmReport ReportWith(params NmSegment[] segments)
    {
        var window = new NmWindow(0, 10, "full");
        var message = new NmMessageReport(
            "0x500", false, "NM", "A", 3, 0, 2,
            new NmCycleStats(500, 500, 500, 500, 0, 0),
            segments);
        return new NmReport(
            window, "src", null,
            Array.Empty<NmEpisode>(),
            new[] { message },
            Array.Empty<NmNodeReport>(),
            Array.Empty<NmEvent>(),
            Array.Empty<NmFinding>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<NmCapabilityState>(),
            Array.Empty<NmInputMissing>(),
            Array.Empty<NmTruncation>());
    }

    [Fact]
    public void BuildTimelineRows_CopiesEvidenceFieldsOntoVm()
    {
        // 三个字段必须逐字段复制：漏一个，hover 与视觉区分就都拿不到数据。
        var report = ReportWith(new NmSegment(
            0, 1, "repeat",
            Evidence: "measured",
            IsWindowTruncated: true,
            StateBoundaryKnown: false));

        var row = TraceViewerViewModel.BuildTimelineRows(report)[0];
        var vm = row.Segments[0];

        vm.Evidence.Should().Be("measured");
        vm.IsWindowTruncated.Should().BeTrue();
        vm.StateBoundaryKnown.Should().BeFalse();
    }

    [Fact]
    public void BuildTimelineRows_DefaultsMatchSegmentDefaults()
    {
        var report = ReportWith(new NmSegment(0, 1, "active"));

        var vm = TraceViewerViewModel.BuildTimelineRows(report)[0].Segments[0];

        vm.Evidence.Should().Be("inferred");
        vm.IsWindowTruncated.Should().BeFalse();
        vm.StateBoundaryKnown.Should().BeFalse();
    }

    [Fact]
    public void NewKinds_HaveBrushesAndChineseLabels()
    {
        // spec §7.5.4：新 kind 必须同批加笔刷与文案，否则会被 _ => UnknownBrush 画成品红。
        foreach (var kind in new[] { "repeat", "normal", "immediate", "unknown" })
        {
            NmSegmentBrushConverter.BrushFor(kind)
                .Should().NotBeSameAs(NmSegmentBrushConverter.UnknownBrush,
                    $"kind {kind} 必须有专属笔刷");
            NmTimelineText.SegmentKind(kind)
                .Should().NotBe(kind, $"kind {kind} 必须有中文文案");
        }
    }

    [Fact]
    public void NmTimelineKinds_All_IncludesNewKinds()
    {
        // 覆盖测试枚举 NmTimelineKinds.All 保证没有漏映射（现有契约）。
        NmTimelineKinds.All.Should().Contain(
            new[] { NmTimelineKinds.Repeat, NmTimelineKinds.Normal, NmTimelineKinds.Immediate, NmTimelineKinds.Unknown });
    }
}
