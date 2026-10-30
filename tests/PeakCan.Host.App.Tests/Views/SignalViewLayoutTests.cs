using System.IO;
using System.Linq;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace PeakCan.Host.App.Tests.Views;

/// <summary>
/// Signal 页 P0 空态布局契约：两种"空"要分清——
/// 还没解码出信号（无动作）vs 搜索把信号全过滤掉了（[清除搜索] 一键恢复）。
/// 面板必须与表格同格叠放，且排在表格之后（否则被表格盖住）。
/// </summary>
public sealed class SignalViewLayoutTests
{
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static XDocument LoadXaml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PeakCan.Host.slnx")))
            dir = dir.Parent!;
        return XDocument.Load(Path.Combine(
            dir!.FullName, "src", "PeakCan.Host.App", "Views", "SignalView.xaml"));
    }

    [Fact]
    public void Empty_State_Panels_Overlay_The_Grid()
    {
        var grid = LoadXaml().Descendants(P + "DataGrid")
            .Single(d => (string?)d.Attribute("ItemsSource") == "{Binding FilteredSignals}");
        var host = grid.Parent!;

        host.Name.LocalName.Should().Be("Grid", "空态面板必须与表格同格叠放");
        var panels = host.Elements().Where(e => e.Name.LocalName == "EmptyStatePanel").ToList();
        panels.Should().HaveCount(2, "无数据 / 无搜索结果 是两种不同空态");

        host.Elements().Last().Name.LocalName.Should().Be("EmptyStatePanel",
            "面板必须排在表格之后，否则被表格盖住");
    }

    [Fact]
    public void No_Data_Panel_Has_No_Action_And_Explains_Prerequisite()
    {
        var panels = LoadXaml().Descendants()
            .Where(e => e.Name.LocalName == "EmptyStatePanel")
            .ToList();

        var noData = panels.Single(p =>
            ((string?)p.Attribute("Title"))?.Contains("还没有解码出信号") == true);

        noData.Attribute("ActionCommand").Should().BeNull("没有前置条件可自动满足，不放按钮");
        ((string?)noData.Attribute("Description")).Should().Contain("DBC");
        ((string?)noData.Attribute("Description")).Should().Contain("通道");

        // 触发条件必须绑 VM 判据（写错属性名会让面板永不出现，而这条断言要能发现）。
        var style = noData.Elements()
            .SingleOrDefault(e => e.Name.LocalName == "EmptyStatePanel.Style")?
            .Element(P + "Style");
        style.Should().NotBeNull();
        style!.Descendants(P + "DataTrigger").Should().Contain(t =>
            (string?)t.Attribute("Binding") == "{Binding IsSignalEmpty}" &&
            (string?)t.Attribute("Value") == "True");
    }

    [Fact]
    public void No_Match_Panel_Offers_Clear_Search_And_Yields_To_No_Data()
    {
        var panels = LoadXaml().Descendants()
            .Where(e => e.Name.LocalName == "EmptyStatePanel")
            .ToList();

        var noMatch = panels.Single(p =>
            ((string?)p.Attribute("Title"))?.Contains("没有匹配") == true);

        ((string?)noMatch.Attribute("ActionText")).Should().Be("清除搜索");
        ((string?)noMatch.Attribute("ActionCommand"))!.Should().Contain("ClearSearchCommand");

        // 触发条件走 VM 布尔值（Style 里的 ElementName 解析不可靠，失效时面板会静默不出现）。
        // 属性元素挂在控件自身的 xmlns 下（controls:EmptyStatePanel.Style），按 LocalName 取。
        var style = noMatch.Elements()
            .SingleOrDefault(e => e.Name.LocalName == "EmptyStatePanel.Style")?
            .Element(P + "Style");
        style.Should().NotBeNull();
        style!.Descendants(P + "DataTrigger").Should().Contain(t =>
            (string?)t.Attribute("Binding") == "{Binding IsSignalFilteredEmpty}" &&
            (string?)t.Attribute("Value") == "True");
    }
}
