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
