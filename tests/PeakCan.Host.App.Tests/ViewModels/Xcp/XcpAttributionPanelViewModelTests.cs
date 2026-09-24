using System;
using System.Linq;
using A2lEditor.Core.Layout;
using FluentAssertions;
using PeakCan.Host.App.Services.Xcp;
using PeakCan.Host.App.ViewModels.Xcp;
using PeakCan.Host.Core.Xcp.Receive;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels.Xcp;

/// <summary>
/// S3-T6 红测：XcpAttributionPanelViewModel（spec D5 合并归因表逐格落地）。
/// <para>
/// 口径：包侧五值 MissingCause + AcquisitionInterrupted 只走 <c>Gap.Cause</c>，
/// 逐帧归因只走 <c>Gap.ReceiveKind</c>，host 两态（未连总线/被过滤）由
/// T3/T5 生产者直喂，计划空窗显示 <c>Gap.ExpectedMaxDuration</c>——
/// 无第四类黑盒串接，无法映射的条目组合 fail loud。
/// 卡片格归因查询是全局格形状：<see cref="XcpAcquisitionGap"/> 无对象标识字段，
/// 不造对象名映射（S3-T6 口径）。
/// </para>
/// </summary>
public class XcpAttributionPanelViewModelTests
{
    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------


    private static XcpAcquisitionGap PackageGap(string detail, MissingCause cause) =>
        new(XcpAcquisitionGapKind.MissingCauseAttributed, detail, Cause: cause);

    private static XcpAcquisitionGap FrameGap(string detail, XcpReceiveAttributionKind kind) =>
        new(XcpAcquisitionGapKind.ReceiveAttribution, detail, ReceiveKind: kind);

    private static XcpAttributionPanelViewModel NewVm() => new();

    // ------------------------------------------------------------------
    // (a) 六类归因格逐类喂入 → 计数与最近明细正确（按 D5 表映射，无黑盒串接）
    // ------------------------------------------------------------------

    [Fact]
    public void SixCategories_FedOneByOne_MapsPerD5Table_NoCrossCellLeak()
    {
        var vm = NewVm();

        // host 两态：T3/T5 生产者直喂。
        vm.ObserveHostAttribution(XcpHostAttributionCell.UnconnectedBus, "conn: Disconnected");
        vm.ObserveHostAttribution(XcpHostAttributionCell.FilteredOut, "watch filter: Volt not in display set");

        // 包侧 MissingCause（五值之一）。
        vm.ObserveGap(PackageGap("NotAcquired: no address for Rpm", MissingCause.NotAcquired));

        // AcquisitionInterrupted（同 Cause 槽位、独立格）。
        vm.ObserveGap(XcpAcquisitionGap.AcquisitionInterrupted("plan gap timed out"));

        // 逐帧归因（ReceiveKind 之一）。
        vm.ObserveGap(FrameGap("first byte 0xFD is not a DTO", XcpReceiveAttributionKind.UnknownPid));

        // 计划空窗。
        vm.ObserveGap(new XcpAcquisitionGap(XcpAcquisitionGapKind.PlanGapOpened,
            "plan gap window opened", ExpectedMaxDuration: TimeSpan.FromMilliseconds(20)));

        // 逐格断言：计数 1 + 最近明细 = 喂入 Detail。
        vm.CellFor(XcpHostAttributionCell.UnconnectedBus).Count.Should().Be(1);
        vm.CellFor(XcpHostAttributionCell.UnconnectedBus).LastDetail.Should().Be("conn: Disconnected");
        vm.CellFor(XcpHostAttributionCell.FilteredOut).Count.Should().Be(1);
        vm.CellFor(XcpHostAttributionCell.FilteredOut).LastDetail.Should().Be("watch filter: Volt not in display set");

        var notAcquired = vm.CellFor(MissingCause.NotAcquired);
        notAcquired.Should().NotBeNull();
        notAcquired!.Count.Should().Be(1);
        notAcquired.LastDetail.Should().Be("NotAcquired: no address for Rpm");
        notAcquired.Kind.Should().Be(XcpAttributionCellKind.PackageMissingCause);
        notAcquired.MissingCause.Should().Be(MissingCause.NotAcquired);

        vm.AcquisitionInterruptedCell.Count.Should().Be(1);
        vm.AcquisitionInterruptedCell.LastDetail.Should().Be("plan gap timed out");
        vm.AcquisitionInterruptedCell.Kind.Should().Be(XcpAttributionCellKind.AcquisitionInterrupted);

        var unknownPid = vm.CellFor(XcpReceiveAttributionKind.UnknownPid);
        unknownPid.Should().NotBeNull();
        unknownPid!.Count.Should().Be(1);
        unknownPid.LastDetail.Should().Be("first byte 0xFD is not a DTO");
        unknownPid.Kind.Should().Be(XcpAttributionCellKind.FrameAttribution);
        unknownPid.ReceiveKind.Should().Be(XcpReceiveAttributionKind.UnknownPid);

        vm.PlanGapCell.Count.Should().Be(1);
        vm.PlanGapCell.LastDetail.Should().Be("plan gap window opened");
        vm.PlanGapCell.LastExpectedMaxDuration.Should().Be(TimeSpan.FromMilliseconds(20));
        vm.PlanGapCell.Kind.Should().Be(XcpAttributionCellKind.PlanGap);

        // 无黑盒串接：恰好 6 格非零（host 两格 + 四类包/Receive 格各一格），
        // 其余格全部保持 0。
        vm.Cells.Where(c => c.Count > 0).Should().HaveCount(6);
        vm.Cells.Where(c => c.Count > 0).Select(c => c.Kind).Should().OnlyHaveUniqueItems();
    }

    // ------------------------------------------------------------------
    // (b) MissingCause 五值 + AcquisitionInterrupted 走 Gap.Cause
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(MissingCause.NotAcquired)]
    [InlineData(MissingCause.SegmentMissing)]
    [InlineData(MissingCause.ConversionUnsupported)]
    [InlineData(MissingCause.AccessBlocked)]
    [InlineData(MissingCause.AccessInferred)]
    public void PackageMissingCause_FiveValues_RouteByGapCause(MissingCause cause)
    {
        var vm = NewVm();

        vm.ObserveGap(PackageGap($"{cause}: first", cause));
        vm.ObserveGap(PackageGap($"{cause}: second", cause));

        var cell = vm.CellFor(cause);
        cell.Should().NotBeNull();
        cell!.Count.Should().Be(2, "同 cause 两条都并入该格");
        cell.LastDetail.Should().Be($"{cause}: second", "最近一条明细覆盖");

        // 其余 cause 格不受污染（无黑盒串接）。
        vm.Cells.Where(c => c.Kind == XcpAttributionCellKind.PackageMissingCause)
            .Should().ContainSingle(c => c.Count > 0);
    }

    [Fact]
    public void AcquisitionInterrupted_GoesToOwnCell_NotPackageMissingCauseCell()
    {
        var vm = NewVm();

        vm.ObserveGap(XcpAcquisitionGap.AcquisitionInterrupted("acquisition interrupted"));

        vm.AcquisitionInterruptedCell.Count.Should().Be(1);
        vm.AcquisitionInterruptedCell.LastDetail.Should().Be("acquisition interrupted");
        // Cause=AcquisitionInterrupted 不落包侧五值格（该槽位无格）。
        vm.CellFor(MissingCause.AcquisitionInterrupted).Should().BeNull();
        vm.Cells.Where(c => c.Kind == XcpAttributionCellKind.PackageMissingCause)
            .Should().OnlyContain(c => c.Count == 0);
    }

    // ------------------------------------------------------------------
    // (c) 逐帧归因走 Gap.ReceiveKind
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(XcpReceiveAttributionKind.MalformedFrame)]
    [InlineData(XcpReceiveAttributionKind.UnknownPid)]
    [InlineData(XcpReceiveAttributionKind.UnmappedDto)]
    [InlineData(XcpReceiveAttributionKind.DecodeFailed)]
    [InlineData(XcpReceiveAttributionKind.CallbackFailed)]
    [InlineData(XcpReceiveAttributionKind.LocalFrameDrop)]
    public void FrameAttribution_RouteByReceiveKind(XcpReceiveAttributionKind kind)
    {
        var vm = NewVm();

        vm.ObserveGap(FrameGap($"{kind}: frame detail", kind));

        var cell = vm.CellFor(kind);
        cell.Should().NotBeNull();
        cell!.Count.Should().Be(1);
        cell.LastDetail.Should().Be($"{kind}: frame detail");
        cell.ReceiveKind.Should().Be(kind);

        // 其余逐帧格不受污染。
        vm.Cells.Where(c => c.Kind == XcpAttributionCellKind.FrameAttribution)
            .Should().ContainSingle(c => c.Count > 0);
    }

    // ------------------------------------------------------------------
    // (d) host 两态由输入直喂（模拟 T3/T5 生产者）
    // ------------------------------------------------------------------

    [Fact]
    public void Host_TwoStates_DirectFeed_FromT3T5Producers()
    {
        var vm = NewVm();

        // T3 生产者：连接状态机非 Connected → UnconnectedBus。
        vm.ObserveHostAttribution(XcpHostAttributionCell.UnconnectedBus, "ConnectionState != Connected");
        vm.ActiveHostCell.Should().Be(XcpHostAttributionCell.UnconnectedBus);
        vm.CellFor(XcpHostAttributionCell.UnconnectedBus).Count.Should().Be(1);

        // T5 生产者：关注集筛选 → FilteredOut。
        vm.ObserveHostAttribution(XcpHostAttributionCell.FilteredOut, "object filtered from display set");
        vm.ActiveHostCell.Should().Be(XcpHostAttributionCell.FilteredOut);
        vm.CellFor(XcpHostAttributionCell.FilteredOut).Count.Should().Be(1);
        vm.CellFor(XcpHostAttributionCell.FilteredOut).LastDetail.Should().Be("object filtered from display set");

        // T3 Connected 态：host 格退场（AttributionCell = null），计数保留。
        vm.ObserveHostAttribution(null);
        vm.ActiveHostCell.Should().BeNull();
        vm.CellFor(XcpHostAttributionCell.UnconnectedBus).Count.Should().Be(1);
        vm.CellFor(XcpHostAttributionCell.FilteredOut).Count.Should().Be(1);
    }

    // ------------------------------------------------------------------
    // 卡片格归因查询接口：对象名 → 当前归因（全局格形状）
    // ------------------------------------------------------------------

    [Fact]
    public void GetAttributionFor_ObjectName_GlobalCell_HostOverridesThenLastGap()
    {
        var vm = NewVm();

        // host 格激活 → 任意对象名都归到当前 host 格。
        vm.ObserveHostAttribution(XcpHostAttributionCell.UnconnectedBus, "conn");
        vm.GetAttributionFor("Rpm").Should().BeSameAs(vm.CellFor(XcpHostAttributionCell.UnconnectedBus));
        vm.GetAttributionFor("Volt").Should().BeSameAs(vm.CellFor(XcpHostAttributionCell.UnconnectedBus));

        // host 格退场 → 回落最近一条 gap 格（Gap 无对象标识字段，全局格显示）。
        vm.ObserveHostAttribution(null);
        vm.GetAttributionFor("Rpm").Should().BeNull();

        vm.ObserveGap(PackageGap("AccessBlocked: locked", MissingCause.AccessBlocked));
        vm.GetAttributionFor("Rpm").Should().BeSameAs(vm.CellFor(MissingCause.AccessBlocked));

        // 对象名只作查询键：同 Gap 流之后任意名字都取同一全局格。
        vm.GetAttributionFor("AnyObject").Should().BeSameAs(vm.CellFor(MissingCause.AccessBlocked));
    }

    // ------------------------------------------------------------------
    // (e) GapObserved 事件流消费（订阅 T5 VM 事件，与 T5 接力不丢条）
    // ------------------------------------------------------------------

    [Fact]
    public void GapObserved_EventRelay_CountsGrowWithoutLoss_NoSecondDrain()
    {
        var sink = new XcpCardPanelSink();
        var cardPanel = new XcpCardPanelViewModel(sink);
        var vm = new XcpAttributionPanelViewModel(cardPanel);

        var gaps = new[]
        {
            PackageGap("NotAcquired: a", MissingCause.NotAcquired),
            PackageGap("SegmentMissing: b", MissingCause.SegmentMissing),
            XcpAcquisitionGap.AcquisitionInterrupted("interrupted"),
            FrameGap("malformed", XcpReceiveAttributionKind.MalformedFrame),
            FrameGap("callback", XcpReceiveAttributionKind.CallbackFailed),
            new XcpAcquisitionGap(XcpAcquisitionGapKind.PlanGapOpened, "plan gap",
                ExpectedMaxDuration: TimeSpan.FromMilliseconds(5)),
        };
        foreach (var gap in gaps)
            sink.OnGap(gap);

        // drain 前不消费（接力只发生在 T5 Flush drain 时）。
        vm.Cells.Sum(c => c.Count).Should().Be(0);

        cardPanel.Flush();

        vm.Cells.Sum(c => c.Count).Should().Be(gaps.Length, "N 条不丢条");
        vm.CellFor(MissingCause.NotAcquired)!.Count.Should().Be(1);
        vm.CellFor(MissingCause.SegmentMissing)!.Count.Should().Be(1);
        vm.AcquisitionInterruptedCell.Count.Should().Be(1);
        vm.CellFor(XcpReceiveAttributionKind.MalformedFrame)!.Count.Should().Be(1);
        vm.CellFor(XcpReceiveAttributionKind.CallbackFailed)!.Count.Should().Be(1);
        vm.PlanGapCell.Count.Should().Be(1);
        vm.PlanGapCell.LastExpectedMaxDuration.Should().Be(TimeSpan.FromMilliseconds(5));

        // 不重复消费：事件旁路后再次 Flush（队列已空）计数不变；
        // 归因 VM 也没有另开 drain（T5 队列此时已空）。
        cardPanel.Flush();
        vm.Cells.Sum(c => c.Count).Should().Be(gaps.Length);
        sink.Drain().Should().BeEmpty("归因 VM 不得另开 sink drain（重复消费即丢条）");
    }

    [Fact]
    public void GapObserved_ContinuousFeed_N_Gaps_CountEqualsN()
    {
        var sink = new XcpCardPanelSink();
        var cardPanel = new XcpCardPanelViewModel(sink);
        var vm = new XcpAttributionPanelViewModel(cardPanel);

        const int n = 50;
        for (var i = 0; i < n; i++)
            sink.OnGap(FrameGap($"drop {i}", XcpReceiveAttributionKind.LocalFrameDrop));

        cardPanel.Flush();

        vm.CellFor(XcpReceiveAttributionKind.LocalFrameDrop)!.Count.Should().Be(n);
        vm.CellFor(XcpReceiveAttributionKind.LocalFrameDrop)!.LastDetail.Should().Be($"drop {n - 1}");
        vm.Cells.Sum(c => c.Count).Should().Be(n);
    }

    [Fact]
    public void Attach_SamePanelTwice_NoDuplicateSubscription()
    {
        var sink = new XcpCardPanelSink();
        var cardPanel = new XcpCardPanelViewModel(sink);
        var vm = new XcpAttributionPanelViewModel();

        vm.Attach(cardPanel);
        vm.Attach(cardPanel);

        sink.OnGap(FrameGap("once", XcpReceiveAttributionKind.UnknownPid));
        cardPanel.Flush();

        vm.CellFor(XcpReceiveAttributionKind.UnknownPid)!.Count.Should().Be(1, "同面板重复 Attach 不得重复订阅");
    }

    [Fact]
    public void Attach_DifferentPanel_FailsLoud()
    {
        var vm = new XcpAttributionPanelViewModel(new XcpCardPanelViewModel(new XcpCardPanelSink()));

        var act = () => vm.Attach(new XcpCardPanelViewModel(new XcpCardPanelSink()));

        act.Should().Throw<InvalidOperationException>("多生产者接力 = 重复消费即丢条，构造期 fail loud");
    }

    // ------------------------------------------------------------------
    // 无法映射的条目组合 fail loud（无第四类黑盒串接）
    // ------------------------------------------------------------------

    [Fact]
    public void ReceiveAttribution_WithoutReceiveKind_FailsLoud()
    {
        var vm = NewVm();
        var gap = new XcpAcquisitionGap(XcpAcquisitionGapKind.ReceiveAttribution, "no receive kind");

        var act = () => vm.ObserveGap(gap);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MissingCauseAttributed_WithoutCause_FailsLoud()
    {
        var vm = NewVm();
        var gap = new XcpAcquisitionGap(XcpAcquisitionGapKind.MissingCauseAttributed, "no cause");

        var act = () => vm.ObserveGap(gap);

        act.Should().Throw<InvalidOperationException>();
    }
}
