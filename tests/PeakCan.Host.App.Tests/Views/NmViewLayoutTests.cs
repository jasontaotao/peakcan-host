using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace PeakCan.Host.App.Tests.Views;

/// <summary>
/// NM 面板与补全对话框的布局契约（结构断言）：
/// 右侧面板默认只有 360px、补全窗最小 900×560；内容再多也不能把“详情”列
/// 挤成 0 宽、把状态文案裁掉，或让主操作跟着编辑区滚出视野。
/// </summary>
public sealed class NmViewLayoutTests
{
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly string[] PinnedActions = { "试算当前窗口", "保存到项目", "取消" };

    private static XDocument Load(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PeakCan.Host.slnx")))
            dir = dir.Parent!;
        return XDocument.Load(Path.Combine(dir!.FullName, relativePath));
    }

    private static XDocument Panel() => Load("src/PeakCan.Host.App/Views/TraceViewerViewNmPanel.xaml");

    private static XDocument Wizard() => Load("src/PeakCan.Host.App/Views/NmInputWizardWindow.xaml");

    [Fact]
    public void Panel_EventTable_LeavesWidthForDetailColumn()
    {
        var grid = Panel().Descendants(P + "DataGrid").Single();
        var columns = grid.Element(P + "DataGrid.Columns")!.Elements(P + "DataGridTextColumn").ToList();

        // 360px 面板：固定列总和必须给 * 列留出可读宽度（≥ 80px）。
        var fixedWidth = columns
            .Select(c => (string?)c.Attribute("Width"))
            .Where(w => w is not null && w != "*")
            .Select(w => double.Parse(w!, CultureInfo.InvariantCulture))
            .Sum();

        (360 - fixedWidth).Should().BeGreaterThanOrEqualTo(80,
            "详情列是事件表的主要信息载体，不能被固定列挤成 0 宽");
        columns.Should().Contain(c => (string?)c.Attribute("Width") == "*");
        columns.Single(c => (string?)c.Attribute("Width") == "*")
            .Descendants(P + "Setter")
            .Should().Contain(s => (string?)s.Attribute("Property") == "TextTrimming");
    }

    [Fact]
    public void Panel_TimelineAndEventTable_ShareRemainingHeight()
    {
        var root = Panel().Descendants(P + "Grid").First();
        var rows = root.Element(P + "Grid.RowDefinitions")!.Elements(P + "RowDefinition").ToList();

        rows.Should().HaveCount(9);
        // 时间条与事件表都按比例分配；事件表不再固定 160px。
        ((string?)rows[5].Attribute("Height")).Should().Be("2*");
        ((string?)rows[5].Attribute("MinHeight")).Should().NotBeNull();
        ((string?)rows[8].Attribute("Height")).Should().Be("*");
        ((string?)rows[8].Attribute("MinHeight")).Should().NotBeNull();
    }

    [Fact]
    public void Panel_FiltersAndActions_WrapInsteadOfClipping()
    {
        var root = Panel().Descendants(P + "Grid").First();

        var filters = root.Elements()
            .Single(e => (string?)e.Attribute("Grid.Row") == "6");
        filters.Name.Should().Be(P + "WrapPanel", "360px 下横向 StackPanel 会把计数裁掉");

        var actions = root.Elements()
            .Single(e => (string?)e.Attribute("Grid.Row") == "7");
        actions.Name.Should().Be(P + "StackPanel");
        actions.Elements(P + "WrapPanel").Should().ContainSingle();
        // 状态文案独立成行，不跟随按钮换行被挤出面板。
        actions.Elements(P + "TextBlock").Should().ContainSingle();
    }

    [Fact]
    public void Panel_ConflictBar_IsHeightCapped()
    {
        var conflictScroll = Panel().Descendants(P + "ScrollViewer")
            .Single(s => (string?)s.Attribute("MaxHeight") == "72");
        conflictScroll.Should().NotBeNull("冲突条数不可控，必须限高滚动");
    }

    [Fact]
    public void Wizard_HasUsableMinimumSizeAndByteColumns()
    {
        var window = Wizard().Root!;
        double.Parse((string)window.Attribute("MinWidth")!, CultureInfo.InvariantCulture)
            .Should().BeGreaterThanOrEqualTo(900);
        double.Parse((string)window.Attribute("MinHeight")!, CultureInfo.InvariantCulture)
            .Should().BeGreaterThanOrEqualTo(560);

        var columns = window.Descendants(P + "Grid").First()
            .Element(P + "Grid.ColumnDefinitions")!.Elements(P + "ColumnDefinition").ToList();
        ((string?)columns[1].Attribute("Width")).Should().Be("360",
            "中栏要容纳 86px 标签 + 5~6 个 48px 字节按钮，330 会让每帧拆成两行");
    }

    [Fact]
    public void Wizard_PrimaryActions_ArePinnedOutsideEditorScroll()
    {
        var editorScroll = Wizard().Descendants(P + "ScrollViewer")
            .Single(s => (string?)s.Attribute("Grid.Row") == "2");
        editorScroll.Descendants(P + "Button")
            .Should().NotContain(b => (string?)b.Attribute("Content") == "保存到项目",
                "保存/取消不能跟着编辑区滚走");

        var actions = Wizard().Descendants(P + "WrapPanel")
            .Single(w => (string?)w.Attribute("Grid.Row") == "3");
        actions.Descendants(P + "Button")
            .Select(b => (string?)b.Attribute("Content"))
            .Should().Contain(PinnedActions);
    }
}
