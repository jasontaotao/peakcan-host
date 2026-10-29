using System.IO;
using System.Linq;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace PeakCan.Host.App.Tests.Views;

public sealed class HilViewLayoutTests
{
    private static string ResolveHilViewPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PeakCan.Host.slnx")))
            dir = dir.Parent!;
        return Path.Combine(dir!.FullName, "src", "PeakCan.Host.App", "Views", "HilView.xaml");
    }

    [Fact]
    public void HilView_DoesNotContainLegacyEnglishButtonCopy()
    {
        var xaml = File.ReadAllText(ResolveHilViewPath());
        xaml.Should().NotContain("Content=\"Mode:\"");
        xaml.Should().NotContain("Content=\"Run\"");
        xaml.Should().NotContain("Content=\"Browse...\"");
        xaml.Should().NotContain("Content=\"Faults\"");
        xaml.Should().NotContain("Content=\"Analyze\"");
        xaml.Should().NotContain("Content=\"Open ECU Editor\"");
        xaml.Should().Contain("Content=\"停止\"");
        xaml.Should().NotContain("Header=\"Test Cases\"");
        xaml.Should().NotContain("Header=\"Results\"");
        xaml.Should().NotContain("Header=\"HTML Report\"");
    }

    [Fact]
    public void HilView_HasStopTrialDiagnosticsAndHistory()
    {
        var xaml = File.ReadAllText(ResolveHilViewPath());
        xaml.Should().Contain("Command=\"{Binding StopCommand}\"");
        xaml.Should().Contain("ItemsSource=\"{Binding TrialDiagnostics}\"");
        xaml.Should().Contain("Text=\"{Binding CaseLogDirectory}\"");
    }

    [Fact]
    public void Cases_Tab_Has_Empty_State_With_Browse_Action()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PeakCan.Host.slnx")))
            dir = dir.Parent!;
        var doc = XDocument.Load(Path.Combine(
            dir!.FullName, "src", "PeakCan.Host.App", "Views", "HilView.xaml"));

        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var grid = doc.Descendants(p + "DataGrid")
            .Single(d => (string?)d.Attribute("ItemsSource") == "{Binding FilteredCases}");
        var host = grid.Parent!;

        host.Name.LocalName.Should().Be("Grid", "空态面板必须与用例表格同格叠放");
        var panels = host.Elements().Where(e => e.Name.LocalName == "EmptyStatePanel").ToList();
        panels.Should().HaveCount(2, "未加载 / 被筛选挡住 是两种不同空态");
        host.Elements().Last().Name.LocalName.Should().Be("EmptyStatePanel",
            "面板必须排在表格之后，否则被表格盖住");

        var panel = panels.Single(p => ((string?)p.Attribute("Title"))?.Contains("还没有加载") == true);
        ((string?)panel.Attribute("Title"))!.Should().Contain("还没有加载测试用例");
        ((string?)panel.Attribute("ActionText")).Should().Be("浏览测试套件");
        ((string?)panel.Attribute("ActionCommand"))!.Should().Contain("BrowseSuiteCommand");

        // 属性元素挂在控件自身的 xmlns 下（controls:EmptyStatePanel.Style），按 LocalName 取。
        var style = panel.Elements()
            .SingleOrDefault(e => e.Name.LocalName == "EmptyStatePanel.Style")?
            .Element(p + "Style");
        style.Should().NotBeNull();
        style!.Descendants(p + "DataTrigger").Should().Contain(t =>
            (string?)t.Attribute("Binding") == "{Binding IsCasesEmpty}" &&
            (string?)t.Attribute("Value") == "True");

        // "有数据但被筛选挡住"是另一半空态：用例页是三个页面里唯一自带筛选框的，
        // 只做"没数据"会让用户面对一张无提示的空表。
        var noMatch = host.Elements()
            .Where(e => e.Name.LocalName == "EmptyStatePanel")
            .Single(e => ((string?)e.Attribute("Title"))?.Contains("没有匹配") == true);
        ((string?)noMatch.Attribute("ActionText")).Should().Be("清除筛选");
        ((string?)noMatch.Attribute("ActionCommand"))!.Should().Contain("ClearCaseFilterCommand");
        noMatch.Elements()
            .Single(e => e.Name.LocalName == "EmptyStatePanel.Style")
            .Descendants(p + "DataTrigger").Should().Contain(t =>
                (string?)t.Attribute("Binding") == "{Binding IsCasesFilteredEmpty}" &&
                (string?)t.Attribute("Value") == "True");
    }

    [Fact]
    public void HilView_HasHistoryRowActionsAndEmptyState()
    {
        var xaml = File.ReadAllText(ResolveHilViewPath());
        xaml.Should().Contain("OpenHistoryReportCommand");
        xaml.Should().Contain("LoadHistorySuiteCommand");
        xaml.Should().Contain("尚无运行记录");
        xaml.Should().Contain("{Binding CurrentCaseName");
        xaml.Should().Contain("{Binding RunElapsedText");
        xaml.Should().Contain("ContextMenuOpening=\"StepContextMenu_Opening\"");
    }

}