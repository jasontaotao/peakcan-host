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

    /// <summary>取某个 DataTrigger 的 Foreground Setter 色值；结构缺失时返回 null 让断言失败。</summary>
    private static string? ForegroundOf(IEnumerable<XElement> triggers, string value)
        => triggers.SingleOrDefault(t => (string?)t.Attribute("Value") == value)
            ?.Elements(P + "Setter")
            .SingleOrDefault(s => (string?)s.Attribute("Property") == "Foreground")
            ?.Attribute("Value")?.Value;

    [Fact]
    public void Panel_EventTable_LeavesWidthForDetailColumn()
    {
        var grid = Panel().Descendants(P + "DataGrid").Single();
        var columns = grid.Element(P + "DataGrid.Columns")!.Elements()
            .Where(e => e.Name == P + "DataGridTextColumn" || e.Name == P + "DataGridTemplateColumn")
            .ToList();

        // 360px 面板：固定列总和必须给 * 列留出可读宽度（≥ 80px）。
        var fixedWidth = columns
            .Select(c => (string?)c.Attribute("Width"))
            .Where(w => w is not null && w != "*")
            .Select(w => double.Parse(w!, CultureInfo.InvariantCulture))
            .Sum();

        (360 - fixedWidth).Should().BeGreaterThanOrEqualTo(80,
            "详情列是事件表的主要信息载体，不能被固定列挤成 0 宽");

        // 证据级别并入详情列：省掉 52px 固定列，详情列用模板列承载图标 + 文本。
        var detail = columns.Single(c => (string?)c.Attribute("Width") == "*");
        detail.Name.Should().Be(P + "DataGridTemplateColumn",
            "详情列要在文本前放证据图标，必须是模板列");
        detail.Descendants(P + "TextBlock")
            .Should().Contain(t => (string?)t.Attribute("Text") == "{Binding EvidenceGlyph}");

        var detailText = detail.Descendants(P + "TextBlock")
            .Single(t => (string?)t.Attribute("Text") == "{Binding Detail}");
        ((string?)detailText.Attribute("TextTrimming")).Should().Be("CharacterEllipsis",
            "详情列是 * 列：长文本必须省略，不能撑出横向滚动");

        // DockPanel 默认 LastChildFill：详情必须是最后一个子元素，
        // 才能拿到剩余宽度让 TextTrimming 生效；图标必须停靠在左侧。
        var panel = detail.Descendants(P + "DockPanel").Single();
        var children = panel.Elements().ToList();
        children.Last().Should().Be(detailText);
        ((string?)children.First().Attribute("DockPanel.Dock")).Should().Be("Left");

        // 证据级别颜色语义：measured 绿 / inferred 橙；颜色对调或写错会静默失去区分度。
        var triggers = detail.Descendants(P + "DataTrigger")
            .Where(t => (string?)t.Attribute("Binding") == "{Binding EvidenceKind}")
            .ToList();
        ForegroundOf(triggers, "measured").Should().Be("#1A7F37");
        ForegroundOf(triggers, "inferred").Should().Be("#B26A00");
    }

    [Fact]
    public void Panel_EventTable_HasNoSeparateEvidenceColumn()
    {
        var grid = Panel().Descendants(P + "DataGrid").Single();
        var headers = grid.Descendants()
            .Select(e => (string?)e.Attribute("Header"))
            .Where(h => h is not null)
            .ToList();

        headers.Should().NotContain("证据",
            "证据列已并入详情列前的图标；再拆列会把详情挤窄");
    }

    [Fact]
    public void Panel_Legend_IsCollapsedByDefault()
    {
        var legend = Panel().Descendants(P + "Expander")
            .Single(e => (string?)e.Attribute("Header") == "图例");

        ((string?)legend.Attribute("IsExpanded")).Should().Be("False",
            "图例常驻三行会吃掉 360px 面板约 60px 垂直空间；默认折叠");

        // 折叠内容不能被删掉，且颜色必须与轨道同源（x:Static 笔刷），不能写死色值。
        legend.Descendants(P + "TextBlock").Should().HaveCount(3);
        var runs = legend.Descendants(P + "Run").ToList();
        runs.Count(r => ((string?)r.Attribute("Foreground"))?
                .StartsWith("{x:Static conv:NmSegmentBrushConverter.", StringComparison.Ordinal) == true)
            .Should().BeGreaterThanOrEqualTo(10,
                "图例色块必须引用转换器笔刷，否则时间条改色后图例会失真");
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
        // 状态文案与“面板=事实 / AI=解释”的分工说明各占一行，
        // 不跟随按钮换行被挤出面板。
        var actionTexts = actions.Elements(P + "TextBlock").ToList();
        actionTexts.Should().HaveCount(2);
        actionTexts.Select(t => (string?)t.Attribute("Text"))
            .Should().Contain("{Binding NmStatus}");
        actionTexts.Select(t => (string?)t.Attribute("Text"))
            .Should().Contain(t => !string.IsNullOrEmpty(t) && t.Contains("AI 负责解释"));
    }

    [Fact]
    public void Panel_ProfileBarAndEmptyHint_ReactToReportState()
    {
        var root = Panel().Descendants(P + "Grid").First();

        var profileBar = root.Elements(P + "Border")
            .Single(b => (string?)b.Attribute("Grid.Row") == "0");
        ((string?)profileBar.Attribute("Visibility")).Should()
            .Contain("NmHasReport", "分析前不能渲染空的 Profile 条/补全按钮");

        var hint = root.Elements(P + "TextBlock")
            .Single(t => (string?)t.Attribute("Grid.Row") == "5");
        ((string?)hint.Attribute("Text")).Should().Contain("尚未分析");
        hint.Descendants(P + "DataTrigger").Should()
            .Contain(t => (string?)t.Attribute("Binding") == "{Binding NmHasReport}");
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
