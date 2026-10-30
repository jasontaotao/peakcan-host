using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using FluentAssertions;
using PeakCan.Host.App.ViewModels.Xcp;
using Xunit;

namespace PeakCan.Host.App.Tests.Views;

/// <summary>
/// XCP 页状态区的布局与 UX 契约。
/// <para>
/// 事故背景（用户实报）：加载一份 3.1 MB 的 A2L（3000 个对象全地址 0x0）时，
/// <c>CollectCrossChecks</c> 产出 3003 条 note，逐条平铺在 <c>Height="Auto"</c> 行里
/// 把整页撑到几万像素，界面不可操作。修复分两层：VM 侧按类折叠（见
/// <c>XcpStatusLineCollapserTests</c>），视图侧把明细收进默认折叠的 Expander。
/// </para>
/// <para>
/// 这里钉的是 UX 不变式，不是像素：<b>结论行必须在折叠区外</b>（否则「对账拒绝启动」
/// 会被一个收起的箭头吞掉），<b>明细默认收起但标题自报条数</b>（否则用户不知道
/// 里面是 3 条还是 3000 条）。只读 XAML 文本，不碰 WPF 运行时。
/// </para>
/// </summary>
public sealed class XcpViewLayoutTests
{
    private static readonly XNamespace P =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static readonly string[] Panels = { "Connection", "Acquisition" };

    private static XDocument LoadXcpView()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "PeakCan.Host.App", "Views", "Xcp", "XcpView.xaml"));
        return XDocument.Load(path);
    }

    private static XElement StatusListOf(XDocument doc, string viewModel) =>
        doc.Descendants(P + "ItemsControl")
            .Single(e => (string?)e.Attribute("ItemsSource") == $"{{Binding {viewModel}.StatusLines}}");

    [Theory]
    [InlineData("Connection")]
    [InlineData("Acquisition")]
    public void Details_are_collapsed_by_default(string viewModel)
    {
        var expander = StatusListOf(LoadXcpView(), viewModel).Ancestors(P + "Expander").FirstOrDefault();
        expander.Should().NotBeNull($"{viewModel}.StatusLines 的明细应放进可折叠区");

        ((string?)expander!.Attribute("IsExpanded")).Should().BeNullOrEmpty(
            "明细默认收起——用户不想看的时候不该占版面");
    }

    [Theory]
    [InlineData("Connection")]
    [InlineData("Acquisition")]
    public void The_collapsed_header_says_how_much_is_inside(string viewModel)
    {
        var expander = StatusListOf(LoadXcpView(), viewModel).Ancestors(P + "Expander").First();
        var header = (string?)expander.Attribute("Header");

        header.Should().Be($"{{Binding {viewModel}.StatusDetailHeader}}",
            "折叠标题必须展示条数（VM 侧写入「检查提示 N 条」）——只写「详情」等于把清单藏了");

        // 绑定写错在 WPF 里是静默失效：标题会变成空白，箭头孤零零挂着。
        typeof(XcpViewModel).Assembly.GetType($"PeakCan.Host.App.ViewModels.Xcp.Xcp{viewModel}PanelViewModel")!
            .GetProperty("StatusDetailHeader", BindingFlags.Public | BindingFlags.Instance)
            .Should().NotBeNull();
    }

    [Theory]
    [InlineData("Connection")]
    [InlineData("Acquisition")]
    public void An_empty_detail_list_hides_the_expander_entirely(string viewModel)
    {
        var expander = StatusListOf(LoadXcpView(), viewModel).Ancestors(P + "Expander").First();

        ((string?)expander.Attribute("Visibility"))
            .Should().Be($"{{Binding {viewModel}.HasStatusDetails, Converter={{StaticResource BoolToVisibility}}}}",
                "没有明细时不该留一个空箭头常驻占位");
    }

    [Theory]
    [InlineData("Connection")]
    [InlineData("Acquisition")]
    public void The_verdict_line_sits_outside_the_expander(string viewModel)
    {
        var summary = LoadXcpView().Descendants(P + "TextBlock")
            .Single(t => (string?)t.Attribute("Text") == $"{{Binding {viewModel}.StatusSummary}}");

        summary.Ancestors(P + "Expander").Should().BeEmpty(
            "结论（加载成功 / 采集已启动 / 对账拒绝启动）必须常驻可见，不能被折叠吞掉");
        typeof(XcpViewModel).Assembly.GetType($"PeakCan.Host.App.ViewModels.Xcp.Xcp{viewModel}PanelViewModel")!
            .GetProperty("StatusSummary", BindingFlags.Public | BindingFlags.Instance)
            .Should().NotBeNull();
    }

    [Theory]
    [InlineData("Connection")]
    [InlineData("Acquisition")]
    public void Expanded_details_stay_height_capped(string viewModel)
    {
        var scroll = StatusListOf(LoadXcpView(), viewModel).Ancestors(P + "ScrollViewer").FirstOrDefault();
        scroll.Should().NotBeNull("展开后仍需滚动容器兜住极端情况");

        var maxHeight = (double?)scroll!.Attribute("MaxHeight");
        maxHeight.Should().NotBeNull("ScrollViewer 不设 MaxHeight 会随内容长高，等于没修");
        maxHeight!.Value.Should().BeInRange(60, 240);
        ((string?)scroll.Attribute("VerticalScrollBarVisibility")).Should().Be("Auto");
    }

    [Theory]
    [InlineData("Connection")]
    [InlineData("Acquisition")]
    public void The_two_red_signals_are_driven_by_separate_flags(string viewModel)
    {
        // 结论行的红色 = "这次动作失败了吗"；折叠标题的红色 = "明细里有没有硬约束"。
        // 两者共用一个布尔时，「A2L 读取失败」会以灰色渲染（看起来像普通提示）。
        var doc = LoadXcpView();

        doc.Descendants()
            .Where(e => (string?)e.Attribute("Binding") == $"{{Binding {viewModel}.HasStatusSummaryError}}")
            .Should().NotBeEmpty("结论行的红色触发必须绑 HasStatusSummaryError");

        doc.Descendants()
            .Where(e => (string?)e.Attribute("Binding") == $"{{Binding {viewModel}.HasStatusError}}")
            .Should().NotBeEmpty("折叠标题的红色触发必须绑 HasStatusError");
    }

    [Fact]
    public void Nothing_forces_the_expanders_open_through_a_style()
    {
        // 只查元素上的 IsExpanded 属性挡不住 Style/Setter 回退——本文件就在用内联 Style。
        LoadXcpView().Descendants()
            .Where(e => (string?)e.Attribute("Property") == "IsExpanded")
            .Should().BeEmpty("明细默认收起，Style 里也不许偷偷设成展开");
    }

    [Fact]
    public void The_status_area_sections_are_independent()
    {
        // 两块明细各用各的 Expander：只想看 A2L 检查时不该被迫连采集明细一起展开。
        var doc = LoadXcpView();
        var expanders = Panels
            .Select(vm => StatusListOf(doc, vm).Ancestors(P + "Expander").First())
            .ToList();

        expanders.Distinct().Should().HaveCount(2);
    }
}
