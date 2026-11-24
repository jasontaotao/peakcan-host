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
    // 冲突夹具：三条不同字段的冲突，用来把顶部信息带撑到自然高度。
    private static readonly string[] CycleValues = { "500", "1000" };
    private static readonly string[] NidByteValues = { "byte0", "byte1" };
    private static readonly string[] TimeoutValues = { "2000", "3000" };
    private static readonly string[] DbcSource = { "DBC" };
    private static readonly string[] DbcAndWizardSource = { "DBC", "补全" };
    private static readonly string[] DbcAndOemSource = { "DBC", "企标" };
    private static readonly string[] ReasonLines = { "识别依据 A", "识别依据 B", "识别依据 C" };

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

    /// <summary>
    /// <b>顶部信息带封顶必须真的生效</b>：XAML 里写了 <c>MaxHeight</c> 不等于布局后
    /// 它真的被限制住——<c>ScrollViewer</c> 套在 <c>Auto</c> 行里时，若测量方式不对，
    /// 它照样会被内容撑到自然高度。这里用真实 Measure/Arrange 量实际占高。
    /// <para>
    /// 同时守住反向契约：时间条与事件表必须拿到可用高度的绝大部分，
    /// 而不是被顶部信息带压到各自的最小值。
    /// </para>
    /// </summary>
    [Fact]
    public void Panel_TopInfoBand_ActuallyCappedAndLeavesRoomForEvidence()
    {
        Exception? caught = null;
        var topHeight = 0.0;
        var timelineHeight = 0.0;
        var tableHeight = 0.0;
        const double panelHeight = 720; // 900px 窗口减去右栏固定 180px 的 J1939 区

        // 必须构造「最重状态」：选择器三条提示 + profile 摘要 + 冲突条 + 语义横幅 +
        // partial + 能力 chip 全部同时出现。夹具太干净时顶部自然高度本就 < 150px，
        // 去掉 MaxHeight 也测不出退化——断言会假绿。
        var snapshot = MakeHeavySnapshot();

        var thread = new Thread(() =>
        {
            try
            {
                LeakedApplicationReset.CleanupLeakedApplication();
                _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

                var vm = NewVm();
                vm.ApplyNmReport(snapshot);
                var panel = new TraceViewerViewNmPanel { DataContext = vm };
                panel.Measure(new Size(360, panelHeight));
                panel.Arrange(new Rect(0, 0, 360, panelHeight));
                panel.UpdateLayout();

                // 按内容定位，不写死行号：行号会随顶部信息带调整而变，
                // 但「封顶区 / 时间条 / 事件表」这三样东西的身份不变。
                // 根 Grid = 直接挂着事件表 DataGrid 的那个 Grid。
                var root = FindDescendant<Grid>(panel, g => g.Children.OfType<DataGrid>().Any());
                root.Should().NotBeNull("面板根 Grid 必须存在（直接挂事件表 DataGrid）");

                var cappedBand = root!.Children.OfType<ScrollViewer>()
                    .SingleOrDefault(s => !double.IsPositiveInfinity(s.MaxHeight) && s.MaxHeight <= 200);
                cappedBand.Should().NotBeNull(
                    "顶部信息带必须是一个带 MaxHeight 的 ScrollViewer；找不到说明封顶丢了");
                topHeight = cappedBand!.ActualHeight;

                // 时间条：包含 Viewbox 轨道的那一行的 ScrollViewer。
                var timeline = root.Children.OfType<ScrollViewer>()
                    .Where(s => s != cappedBand && HasDescendant<Viewbox>(s))
                    .ToList();
                timeline.Should().ContainSingle("时间条所在行应恰好有一个滚动宿主");
                timelineHeight = timeline[0].ActualHeight;

                var table = root.Children.OfType<DataGrid>().Single();
                tableHeight = table.ActualHeight;
            }
            catch (Exception ex) { caught = ex; }
            finally
            {
                try { Application.Current?.Shutdown(); }
                catch { /* dispatcher may already be shutting down */ }
                LeakedApplicationReset.CleanupLeakedApplication();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue("STA 布局线程必须完成");
        if (caught is not null) throw caught;

        topHeight.Should().BeLessThanOrEqualTo(150.5,
            "MaxHeight=150 必须在真实布局后生效，否则顶部信息带仍会无限撑高");

        topHeight.Should().BeGreaterThan(0, "顶部信息带必须真的完成布局，否则上限断言会空过");
        timelineHeight.Should().BeGreaterThan(72,
            "时间条是用户最常看的一块，不能被压到 MinHeight 正好卡住");
        tableHeight.Should().BeGreaterThan(120,
            "事件表同样不能被压到 MinHeight 正好卡住");
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

    private static bool HasDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var children = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < children; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T) return true;
            if (HasDescendant<T>(child)) return true;
        }
        return false;
    }

    private static T? FindDescendant<T>(DependencyObject root, Func<T, bool> predicate)
        where T : DependencyObject
    {
        var children = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < children; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && predicate(match)) return match;
            var deeper = FindDescendant(child, predicate);
            if (deeper is not null) return deeper;
        }
        return null;
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

    /// <summary>
    /// 最重状态的快照：顶部六条信息带全部有内容。夹具太干净时
    /// 「顶部是否真的封顶」测不出来——内容自然高度本就不到上限。
    /// </summary>
    private static NmToolSnapshot MakeHeavySnapshot()
    {
        var caps = new[]
        {
            new NmCapabilityState(NmCapability.NidCheck, false,
                new[] { new NmInputMissing(NmCapability.NidCheck, "nid_byte",
                    "缺少 NID 所在字节位置，无法判定节点 ID 映射", "在补全对话框指定", "measured") }),
            new NmCapabilityState(NmCapability.CbvDecode, false,
                new[] { new NmInputMissing(NmCapability.CbvDecode, "cbv_byte",
                    "缺少 CBV 位置，无法解码控制位向量", null, null) }),
            new NmCapabilityState(NmCapability.PnCheck, false,
                new[] { new NmInputMissing(NmCapability.PnCheck, "pni_role",
                    "未声明 PNI 角色，无法检查部分网络", null, null) }),
            new NmCapabilityState(NmCapability.UserData, true, Array.Empty<NmInputMissing>()),
            new NmCapabilityState(NmCapability.OemRules, true, Array.Empty<NmInputMissing>()),
        };

        var report = new NmReport(
            new NmWindow(0, 10, "full"), "src", null,
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
            caps,
            Array.Empty<NmInputMissing>(),
            Array.Empty<NmTruncation>(),
            IsPartial: true);

        // 三条冲突：冲突条内部有 MaxHeight=72，但标题与边框仍占位。
        var profile = new NmProfile(
            NmProfileSource.Dbc,
            new[] { new NmMessageDef("0x500", false, "NM_A", "ECU_A", 8, true, CannmReasons) },
            new NmTimings(CycleMs: 500, TimeoutMs: 2000),
            Array.Empty<NmNodeDef>(),
            new[]
            {
                new NmConfigConflict("周期", CycleValues, DbcAndWizardSource),
                new NmConfigConflict("NID 字节", NidByteValues, DbcAndWizardSource),
                new NmConfigConflict("超时", TimeoutValues, DbcAndOemSource),
            },
            ReasonLines,
            new NmProvenance());
        return new NmToolSnapshot(
            "src", null, "C:/nets/net.dbc", "hash", profile, report,
            Array.Empty<ReplayFrame>());
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
