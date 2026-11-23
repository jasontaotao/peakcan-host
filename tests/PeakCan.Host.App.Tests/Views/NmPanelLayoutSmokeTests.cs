using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.HIL.Core;
using PeakCan.Host.App.Converters;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.ChatTools;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.Tests.Collections;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.App.Views;
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using ScottPlot;
using Xunit;
using Path = System.Windows.Shapes.Path;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace PeakCan.Host.App.Tests.Views;

/// <summary>
/// NM 面板布局冒烟：真实实例化 UserControl + 真实数据模板 + 360px 面板宽度，
/// 确保时间条/事件轨道/图例能完成 Measure/Arrange。
/// <para>
/// 只构建是无法发现 <c>x:Static</c> 笔刷、命名元素 Trigger、MultiBinding 这类
/// 运行期加载问题的；这里在 STA 上跑一遍布局，并确认事件标记形状真的进入了
/// 视觉树（不是数据有、界面没画）。
/// </para>
/// </summary>
[Collection(WpfAppTestCollection.Name)]
public class NmPanelLayoutSmokeTests
{
    private static readonly string[] NmParticipants = { "0x500" };
    private static readonly string[] CannmReasons = { "MsgType=CANNM" };

    [Fact]
    public void Panel_WithEventMarkers_LaysOutAndPositionsMarkersOnTrack()
    {
        Exception? caught = null;
        var markerPositions = new List<(double Left, double TrackWidth)>();
        var boundaryBarCount = 0;

        var thread = new Thread(() =>
        {
            try
            {
                LeakedApplicationReset.CleanupLeakedApplication();
                _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

                var vm = NewVm();
                vm.ApplyNmReport(MakeSnapshot());
                var panel = new TraceViewerViewNmPanel { DataContext = vm };

                // 右侧面板真实宽度约 360px；给足高度让所有行完成布局。
                panel.Measure(new Size(360, 900));
                panel.Arrange(new Rect(0, 0, 360, 900));
                panel.UpdateLayout();

                markerPositions = FindMarkerPositions(panel);
                boundaryBarCount = CountVisibleBoundaryBars(panel);
            }
            catch (Exception ex)
            {
                caught = ex;
            }
            finally
            {
                try { Application.Current?.Shutdown(); }
                catch { /* dispatcher may already be shutting down */ }
                LeakedApplicationReset.CleanupLeakedApplication();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue(
            "STA 布局线程必须完成——卡住说明模板/绑定把布局挂死了");
        if (caught is not null) throw caught;

        // 两个报文事件：burst@0.5s（设计 X=50）与 byte_change@2.0s（X=200）。
        // 位置断言直接钉住「MultiBinding 取到的是轨道宽度而不是内层 Canvas 的 0」。
        markerPositions.Should().HaveCount(2);
        var (firstLeft, firstWidth) = markerPositions[0];
        var (secondLeft, secondWidth) = markerPositions[1];
        firstWidth.Should().BeGreaterThan(0, "标记轨道必须有实际宽度");
        firstLeft.Should().BeApproximately(50.0 / 1000.0 * firstWidth - 4.5, 0.6);
        secondLeft.Should().BeApproximately(200.0 / 1000.0 * secondWidth - 4.5, 0.6);
        secondLeft.Should().BeGreaterThan(firstLeft, "2.0s 的标记必须排在 0.5s 右侧");

        boundaryBarCount.Should().Be(2,
            "episode 起止都触及窗口边界：两条记录边界竖线必须可见（DataTemplate Trigger 生效）");
    }

    /// <summary>收集可见事件形状的像素位置：左缘 + 所在行轨道宽度。</summary>
    private static List<(double Left, double TrackWidth)> FindMarkerPositions(DependencyObject root)
    {
        var positions = new List<(double Left, double TrackWidth)>();
        Collect(root);
        return positions;

        void Collect(DependencyObject node)
        {
            if (node is Path { Visibility: Visibility.Visible, Data: not null } path
                && IsMarkerGeometry(path.Data))
            {
                var presenter = FindAncestor<ContentPresenter>(path);
                var row = FindAncestor<StackPanel>(path);
                if (presenter is not null && row is not null)
                    positions.Add((Canvas.GetLeft(presenter), row.ActualWidth));
            }

            var children = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < children; i++)
                Collect(VisualTreeHelper.GetChild(node, i));
        }
    }

    private static bool IsMarkerGeometry(Geometry geometry)
        => ReferenceEquals(geometry, NmEventShapeConverter.BurstShape)
           || ReferenceEquals(geometry, NmEventShapeConverter.GapShape)
           || ReferenceEquals(geometry, NmEventShapeConverter.ByteShape)
           || ReferenceEquals(geometry, NmEventShapeConverter.BitShape)
           || ReferenceEquals(geometry, NmEventShapeConverter.SignalShape)
           || ReferenceEquals(geometry, NmEventShapeConverter.UnknownShape);

    private static int CountVisibleBoundaryBars(DependencyObject root)
    {
        var count = root is Rectangle { Visibility: Visibility.Visible } rect
                    && ReferenceEquals(rect.Fill, NmSegmentBrushConverter.BoundaryBrush)
            ? 1
            : 0;
        var children = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < children; i++)
            count += CountVisibleBoundaryBars(VisualTreeHelper.GetChild(root, i));
        return count;
    }

    private static T? FindAncestor<T>(DependencyObject node) where T : DependencyObject
    {
        var current = VisualTreeHelper.GetParent(node);
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static NmToolSnapshot MakeSnapshot()
    {
        var report = new NmReport(
            new NmWindow(0, 10, "full"), "src", null,
            // 起止都触及窗口边界：覆盖边界竖线的 DataTemplate Trigger 分支。
            new[] { new NmEpisode(0, 0, 10, "active", null, NmParticipants, true, true) },
            new[]
            {
                new NmMessageReport("0x500", false, "NM_A", "ECU_A", 4, 0, 10,
                    new NmCycleStats(500, 500, 500, 500, 0, 0, 1, 500, 100),
                    new[] { new NmSegment(0, 10, "active") }),
            },
            Array.Empty<NmNodeReport>(),
            new[]
            {
                new NmEvent(0.5, "burst_observed", "ECU_A", "0x500", "3 帧短间隔", 0, "measured"),
                new NmEvent(2.0, "byte_change", null, "0x500", "byte0: 0x00 → 0x08", 0, "measured"),
            },
            Array.Empty<NmFinding>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<NmCapabilityState>(),
            Array.Empty<NmInputMissing>(),
            Array.Empty<NmTruncation>(),
            IsPartial: false);
        var profile = new NmProfile(
            NmProfileSource.Dbc,
            new[] { new NmMessageDef("0x500", false, "NM_A", "ECU_A", 8, true, CannmReasons) },
            new NmTimings(CycleMs: 500, TimeoutMs: 2000),
            Array.Empty<NmNodeDef>(),
            Array.Empty<NmConfigConflict>(),
            Array.Empty<string>(),
            new NmProvenance());
        return new NmToolSnapshot(
            "src", null, "C:/nets/net.dbc", "hash", profile, report,
            Array.Empty<ReplayFrame>());
    }

    private static TraceViewerViewModel NewVm()
    {
        var session = Substitute.For<ITraceSessionService>();
        session.WatchedSignals.Returns(new ObservableCollection<WatchedSignalRow>());
        session.SignalGroups.Returns(new ObservableCollection<WatchedSignalGroup>());
        var registry = Substitute.For<ITraceSessionRegistry>();
        registry.Sources.Returns(new List<TraceSource>());
        var dbcService = Substitute.For<DbcService>(NullLogger<DbcService>.Instance);
        var libPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"nm-layout-{Guid.NewGuid():N}.tmtrace");
        return new TraceViewerViewModel(session, registry, dbcService,
            NullLogger<TraceViewerViewModel>.Instance,
            new TraceSessionLibrary(libPath, NullLogger<TraceSessionLibrary>.Instance));
    }
}
