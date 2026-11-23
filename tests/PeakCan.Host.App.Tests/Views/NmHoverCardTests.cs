using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.HIL.Core;
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

namespace PeakCan.Host.App.Tests.Views;

/// <summary>
/// 回归（用户报告：悬停事件标记时文字弹不出来）：
/// <see cref="Popup"/> 在 <c>IsOpen=true</c> 之前设置的 DataContext 不会传播进
/// 打开时重建的弹出子树——Child.DataContext 有值，但内部 TextBlock.DataContext
/// 为 null，绑定停在 PathError，卡片是空白的。
/// 本测试用真实窗口 + 真实视觉树锁住「打开后文字真的渲染出来」。
/// </summary>
[Collection(WpfAppTestCollection.Name)]
public class NmHoverCardTests
{
    [Fact]
    public void MarkerMouseEnter_RendersCardTextInsidePopup()
    {
        Exception? caught = null;
        var popupOpened = false;
        var title = "";
        var cardTexts = "";

        var thread = new Thread(() =>
        {
            try
            {
                LeakedApplicationReset.CleanupLeakedApplication();
                _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

                var vm = NewVm();
                vm.ApplyNmReport(MakeSnapshot());
                var panel = new TraceViewerViewNmPanel { DataContext = vm };
                var window = new Window
                {
                    Content = panel,
                    Width = 380,
                    Height = 700,
                    WindowStyle = WindowStyle.None,
                    ShowInTaskbar = false,
                    Left = -2000,
                    Top = -2000,
                };
                window.Show();
                panel.UpdateLayout();

                var marker = FindMarkerGrid(panel);
                marker.Should().NotBeNull("报告有事件时标记必须进入视觉树");

                marker!.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
                {
                    RoutedEvent = UIElement.MouseEnterEvent,
                });

                // Popup 是独立窗口：IsOpen 后要泵一轮 Dispatcher，视觉树才会真正生成。
                DoEvents();
                var popup = (Popup)panel.FindName("NmSegmentPopup");
                popupOpened = popup.IsOpen;
                title = vm.NmHoverTitle;
                popup.Child.UpdateLayout();
                cardTexts = string.Join(" | ", CollectTexts(popup.Child)
                    .Where(t => !string.IsNullOrWhiteSpace(t)));

                window.Close();
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
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue();

        if (caught is not null) throw caught;
        title.Should().NotBeEmpty("MouseEnter 必须把卡片文案写进 VM");
        popupOpened.Should().BeTrue("MouseEnter 必须打开共享信息卡");
        cardTexts.Should().Contain(title,
            "卡片必须把 Title 渲染成可见文字；实际卡片文字：{0}", cardTexts);
        // DataContext 传播覆盖整个弹出子树：三行文案都要出来，不只 Title。
        cardTexts.Should().Contain("0.50s", "时间字段也必须渲染；实际：{0}", cardTexts);
        cardTexts.Should().Contain("3 帧短间隔", "详情字段也必须渲染；实际：{0}", cardTexts);
    }

    [Fact]
    public void MarkerMouseEnter_LongTitle_WrapsInsideCardInsteadOfClipping()
    {
        // 用户报告：卡片标题在 "(" 处被硬裁掉。根因是横向 StackPanel 给标题
        // 无限测量宽度，TextWrapping 不生效，最后被 Border.MaxWidth 裁切。
        Exception? caught = null;
        double titleWidth = 0;
        double innerWidth = 0;
        double titleHeight = 0;
        var wrapped = false;

        var thread = new Thread(() =>
        {
            try
            {
                LeakedApplicationReset.CleanupLeakedApplication();
                _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

                var vm = NewVm();
                vm.ApplyNmReport(MakeSnapshot(
                    name: "BMS_NWM_LONG_NAME", sender: "ECU_SENDER_LONG_NAME"));
                var panel = new TraceViewerViewNmPanel { DataContext = vm };
                var window = new Window
                {
                    Content = panel,
                    Width = 380,
                    Height = 700,
                    WindowStyle = WindowStyle.None,
                    ShowInTaskbar = false,
                    Left = -2000,
                    Top = -2000,
                };
                window.Show();
                panel.UpdateLayout();

                var marker = FindMarkerGrid(panel);
                marker.Should().NotBeNull();
                marker!.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
                {
                    RoutedEvent = UIElement.MouseEnterEvent,
                });

                DoEvents();
                var popup = (Popup)panel.FindName("NmSegmentPopup");
                popup.IsOpen.Should().BeTrue("MouseEnter 必须打开信息卡；否则下面的尺寸断言会误报成文字问题");
                popup.Child.UpdateLayout();

                var card = (Border)popup.Child;
                innerWidth = card.ActualWidth - card.Padding.Left - card.Padding.Right;
                var titleBlock = FindTextBlock(card, vm.NmHoverTitle);
                titleBlock.Should().NotBeNull("标题必须渲染在卡片里");
                titleWidth = titleBlock!.ActualWidth;
                titleHeight = titleBlock.ActualHeight;
                wrapped = titleBlock.TextWrapping == TextWrapping.Wrap;

                window.Close();
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
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue();

        if (caught is not null) throw caught;
        wrapped.Should().BeTrue();
        titleWidth.Should().BeLessThanOrEqualTo(innerWidth + 0.5,
            "标题必须在卡片内容宽度内换行，不能被 MaxWidth 裁切");
        titleHeight.Should().BeGreaterThan(20,
            "长标题应换行成多行，而不是单行溢出");
    }

    /// <summary>泵一轮 Dispatcher：Popup 的独立窗口/视觉树在 IsOpen 后才异步生成。</summary>
    private static void DoEvents()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        // Render 优先级在布局之后、空闲之前：既覆盖弹出子树的布局，也避免慢机空转。
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Render,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static IEnumerable<string> CollectTexts(DependencyObject root)
    {
        if (root is TextBlock tb) yield return tb.Text;
        var children = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < children; i++)
        {
            foreach (var text in CollectTexts(VisualTreeHelper.GetChild(root, i)))
                yield return text;
        }
    }

    private static TextBlock? FindTextBlock(DependencyObject root, string text)
    {
        if (root is TextBlock tb && tb.Text == text) return tb;
        var children = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < children; i++)
        {
            if (FindTextBlock(VisualTreeHelper.GetChild(root, i), text) is { } found)
                return found;
        }
        return null;
    }

    /// <summary>
    /// 必须精确到挂 MouseEnter 的 Grid：ContentPresenter 的 DataContext 相同，
    /// 但对它 RaiseEvent 不会命中子元素的直接事件 handler。
    /// </summary>
    private static Grid? FindMarkerGrid(DependencyObject root)
    {
        if (root is Grid { DataContext: NmTimelineMarkerVm } grid)
            return grid;

        var children = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < children; i++)
        {
            if (FindMarkerGrid(VisualTreeHelper.GetChild(root, i)) is { } found)
                return found;
        }
        return null;
    }

    private static NmToolSnapshot MakeSnapshot(string name = "NM_A", string sender = "ECU_A")
    {
        var reasons = new[] { "MsgType=CANNM" };
        var participants = new[] { "0x500" };
        var report = new NmReport(
            new NmWindow(0, 10, "full"), "src", null,
            new[] { new NmEpisode(0, 0, 10, "active", null, participants, true, true) },
            new[]
            {
                new NmMessageReport("0x500", false, name, sender, 4, 0, 10,
                    new NmCycleStats(500, 500, 500, 500, 0, 0, 1, 500, 100),
                    new[] { new NmSegment(0, 10, "active") }, false),
            },
            Array.Empty<NmNodeReport>(),
            new[]
            {
                new NmEvent(0.5, "burst_observed", sender, "0x500", "3 帧短间隔", 0, "measured"),
            },
            Array.Empty<NmFinding>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<NmCapabilityState>(),
            Array.Empty<NmInputMissing>(),
            Array.Empty<NmTruncation>());
        var profile = new NmProfile(
            NmProfileSource.Dbc,
            new[] { new NmMessageDef("0x500", false, "NM_A", "ECU_A", 8, true, reasons) },
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
            System.IO.Path.GetTempPath(), $"nm-hover-{Guid.NewGuid():N}.tmtrace");
        return new TraceViewerViewModel(session, registry, dbcService,
            NullLogger<TraceViewerViewModel>.Instance,
            new TraceSessionLibrary(libPath, NullLogger<TraceSessionLibrary>.Instance));
    }
}
