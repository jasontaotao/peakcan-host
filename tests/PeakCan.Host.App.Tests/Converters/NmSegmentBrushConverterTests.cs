using System.Globalization;
using FluentAssertions;
using PeakCan.Host.App.Converters;
using Xunit;

namespace PeakCan.Host.App.Tests.Converters;

/// <summary>
/// NM 时间条配色转换器的回归测试。
/// <para>
/// 2026-11-20 生产截图暴露：网络行产生 kind <c>"silence"</c>，而转换器只认
/// <c>"silent"</c>，未知 kind 又兜底成「活动蓝」——整网静默被画成正在发送。
/// 本测试钉住两件事：任一已发射 kind 必须显式映射；未知 kind 必须落到
/// 醒目的「未知色」，绝不伪装成活动。
/// </para>
/// </summary>
public class NmSegmentBrushConverterTests
{
    private readonly NmSegmentBrushConverter _sut = new();

    private object Map(string kind)
        => _sut.Convert(kind, typeof(object), null, CultureInfo.InvariantCulture);

    [Fact]
    public void SilenceKind_MapsToSilentBrush_NotActive()
    {
        // 网络行用的是 "silence"（NmPanelFlow.BuildNetworkTimelineRow），
        // 报文活动段用的是 "silent"（NmActivityBuilder）。两者语义一致。
        Map("silence").Should().BeSameAs(Map("silent"),
            "全网静默段必须与报文静默段同色；落到活动蓝会把「没有报文」画成「正在发送」");
        Map("silence").Should().NotBeSameAs(Map("active"));
    }

    [Fact]
    public void UnknownKind_FallsBackToConspicuousUnknownBrush_NotActive()
    {
        var unknown = Map("a_kind_that_does_not_exist");

        unknown.Should().NotBeSameAs(Map("active"),
            "未知 kind 兜底成活动色会把 bug 伪装成正常数据；必须显眼");
    }

    [Fact]
    public void ReasonValueKinds_MapToDistinctPaletteColors()
    {
        // 原因行的每个原始值必须有独立颜色：整行同色等于没有信息。
        Map(NmTimelineKinds.ReasonValue(0)).Should().NotBeSameAs(Map(NmTimelineKinds.ReasonValue(1)));
        Map(NmTimelineKinds.ReasonValue(2)).Should().NotBeSameAs(Map(NmTimelineKinds.ReasonValue(3)));
        // 同一取值必须稳定复用同一颜色。
        Map(NmTimelineKinds.ReasonValue(3)).Should().BeSameAs(Map(NmTimelineKinds.ReasonValue(3)));
        Map(NmTimelineKinds.ReasonValue(0)).Should().NotBeSameAs(NmSegmentBrushConverter.UnknownBrush,
            "原因值颜色是显式映射，不能落到未知兜底色");
    }

    [Fact]
    public void ReasonPalette_FirstEightValues_AreAllDistinct()
    {
        var brushes = Enumerable.Range(0, 8)
            .Select(i => Map(NmTimelineKinds.ReasonValue(i)))
            .ToArray();

        brushes.Distinct().Should().HaveCount(8,
            "色板内颜色必须互不相同；循环复用只允许发生在第 9 个取值之后");
    }

    [Fact]
    public void ReasonShape_IsUniqueAmongMarkerShapes()
    {
        var shapes = new[]
        {
            NmEventShapeConverter.BurstShape,
            NmEventShapeConverter.GapShape,
            NmEventShapeConverter.ByteShape,
            NmEventShapeConverter.BitShape,
            NmEventShapeConverter.SignalShape,
            NmEventShapeConverter.ReasonShape,
        };

        shapes.Distinct().Should().HaveCount(shapes.Length,
            "原因标记与既有标记形状重复会导致“看不出是原因变化”");
    }

    [Fact]
    public void ReasonUncertainKind_IsVisuallyDistinctFromConfirmedValue()
    {
        Map(NmTimelineKinds.ReasonUncertain(0)).Should()
            .NotBeSameAs(Map(NmTimelineKinds.ReasonValue(0)),
                "未观测区间不能被画成确认值，否则清除区间不可见");
        Map(NmTimelineKinds.ReasonUncertain(0)).Should()
            .NotBeSameAs(NmSegmentBrushConverter.UnknownBrush);
    }

    [Fact]
    public void SilencePhaseKinds_MapToDistinctBrushes_NotSilentOrUnknown()
    {
        var kinds = new[]
        {
            NmTimelineKinds.SilenceReleaseWait,
            NmTimelineKinds.SilencePrepareSleep,
            NmTimelineKinds.SilenceBusSleep,
            NmTimelineKinds.SilencePhaseUnknown,
        };

        var brushes = kinds.Select(Map).ToArray();

        brushes.Distinct().Should().HaveCount(kinds.Length,
            "三种静默相位必须彼此可辨");
        foreach (var brush in brushes)
        {
            brush.Should().NotBeSameAs(Map(NmTimelineKinds.Silence),
                "相位与「原因不可区分」的旧静默带必须区分");
            brush.Should().NotBeSameAs(NmSegmentBrushConverter.UnknownBrush,
                "相位是显式映射，不能落回未知兜底色");
        }
    }

    [Fact]
    public void NonStringValue_FallsBackToUnknownBrush()
    {
        _sut.Convert(42, typeof(object), null, CultureInfo.InvariantCulture)
            .Should().BeSameAs(Map("a_kind_that_does_not_exist"));
    }

    [Fact]
    public void EveryEmittedKind_HasExplicitBrush()
    {
        foreach (var kind in NmTimelineKinds.All)
        {
            NmSegmentBrushConverter.BrushFor(kind).Should().NotBeSameAs(
                NmSegmentBrushConverter.UnknownBrush,
                $"kind「{kind}」已由面板发射，必须显式映射；落回未知色说明常量表与转换器漂移");
        }
    }

    [Fact]
    public void SemanticKinds_MapToDocumentedBrushes()
    {
        NmSegmentBrushConverter.BrushFor(NmTimelineKinds.Wakeup)
            .Should().BeSameAs(NmSegmentBrushConverter.WakeupBrush);
        NmSegmentBrushConverter.BrushFor(NmTimelineKinds.Active)
            .Should().BeSameAs(NmSegmentBrushConverter.ActiveBrush);
        NmSegmentBrushConverter.BrushFor(NmTimelineKinds.Released)
            .Should().BeSameAs(NmSegmentBrushConverter.ReleasedBrush);
        NmSegmentBrushConverter.BrushFor(NmTimelineKinds.Silence)
            .Should().BeSameAs(NmSegmentBrushConverter.SilentBrush);
        NmSegmentBrushConverter.BrushFor(NmTimelineKinds.Episode)
            .Should().BeSameAs(NmSegmentBrushConverter.EpisodeBrush);
        NmSegmentBrushConverter.BrushFor(NmTimelineKinds.Boundary)
            .Should().BeSameAs(NmSegmentBrushConverter.BoundaryBrush);
    }

    [Fact]
    public void ReasonKind_HasDistinctBrushFromUnknown()
    {
        Map(NmTimelineKinds.Reason).Should().BeSameAs(NmSegmentBrushConverter.ReasonBrush);
        Map(NmTimelineKinds.Reason).Should().NotBeSameAs(NmSegmentBrushConverter.UnknownBrush,
            "原因段是正常数据，不能画成未知色");
    }

    [Fact]
    public void ReasonShape_IsRegisteredAndDistinct()
    {
        // 未注册会落到未知六边形（品红），会被误读成异常事件。
        NmEventShapeConverter.ShapeFor(NmTimelineKinds.Reason)
            .Should().BeSameAs(NmEventShapeConverter.ReasonShape);
        NmEventShapeConverter.ShapeFor(NmTimelineKinds.Reason)
            .Should().NotBeSameAs(NmEventShapeConverter.UnknownShape);
    }

    [Fact]
    public void MarkerKinds_HaveDistinctShapes()
    {
        NmEventShapeConverter.ShapeFor(NmTimelineKinds.Burst)
            .Should().BeSameAs(NmEventShapeConverter.BurstShape);
        NmEventShapeConverter.ShapeFor(NmTimelineKinds.Gap)
            .Should().BeSameAs(NmEventShapeConverter.GapShape);
        NmEventShapeConverter.ShapeFor(NmTimelineKinds.BusGap)
            .Should().BeSameAs(NmEventShapeConverter.GapShape,
                "总线空档与超时空档同属「空档」，共用倒三角");
        NmEventShapeConverter.ShapeFor(NmTimelineKinds.Byte)
            .Should().BeSameAs(NmEventShapeConverter.ByteShape);
        NmEventShapeConverter.ShapeFor(NmTimelineKinds.Bit)
            .Should().BeSameAs(NmEventShapeConverter.BitShape);
        NmEventShapeConverter.ShapeFor(NmTimelineKinds.Signal)
            .Should().BeSameAs(NmEventShapeConverter.SignalShape);
        NmEventShapeConverter.ShapeFor("nope")
            .Should().BeSameAs(NmEventShapeConverter.UnknownShape);

        var shapes = new[]
        {
            NmEventShapeConverter.BurstShape, NmEventShapeConverter.GapShape,
            NmEventShapeConverter.ByteShape, NmEventShapeConverter.BitShape,
            NmEventShapeConverter.SignalShape, NmEventShapeConverter.UnknownShape,
        };
        shapes.Should().OnlyHaveUniqueItems("形状是颜色之外的第二编码，不能复用");
    }

    [Fact]
    public void PixelX_CentersNinePixelMarkerAndClampsToTrack()
    {
        var sut = new NmTimelinePixelXConverter();

        double Map(double designX, double width)
            => (double)sut.Convert(new object[] { designX, width }, typeof(double), null,
                CultureInfo.InvariantCulture);

        Map(500, 360).Should().BeApproximately(180 - 4.5, 0.001);
        Map(0, 360).Should().Be(0, "左边缘标记不能画到轨道外");
        Map(1000, 360).Should().BeApproximately(360 - 9, 0.001, "右边缘标记整体收进轨道");
        Map(500, 0).Should().Be(0, "布局未完成（宽 0）时退回 0");
        Map(double.NaN, 360).Should().Be(0);
    }
}
