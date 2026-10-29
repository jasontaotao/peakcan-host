using System.IO;
using System.Linq;
using System.Xml.Linq;
using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.App.Views;
using Xunit;

namespace PeakCan.Host.App.Tests.Views;

/// <summary>
/// Trace 页 P0 布局契约（结构断言，避免整文件 Contains 的假绿）：
/// 1) 错误帧左侧 3px 红条（BorderBrush=Error）——高亮触发器只改 Background；
/// 2) 主 DataGrid 挂行右键菜单与右键先选中的处理器。
/// </summary>
public sealed class TraceViewLayoutTests
{
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string ResolveTraceViewPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PeakCan.Host.slnx")))
            dir = dir.Parent!;
        return Path.Combine(dir!.FullName, "src", "PeakCan.Host.App", "Views", "TraceView.xaml");
    }

    private static XDocument LoadXaml() => XDocument.Load(ResolveTraceViewPath());

    [Fact]
    public void Error_Trigger_Sets_NonOverridable_Stripe()
    {
        var doc = LoadXaml();
        // 页面上不止一个 DataGrid（J1939 会话事件表也有 RowStyle），
        // 必须从主 TraceGrid 定位，避免断言到无关触发器。
        var grid = doc.Descendants(P + "DataGrid")
            .Single(e => (string?)e.Attribute(X + "Name") == "TraceGrid");
        var style = grid.Element(P + "DataGrid.RowStyle")!
            .Element(P + "Style")!;

        // 基座统一预留 3px 左边框（透明）→ 所有行内容不因错误标记位移。
        var baseSetters = style.Elements(P + "Setter").ToList();
        baseSetters.Should().Contain(s =>
            (string?)s.Attribute("Property") == "BorderThickness" &&
            (string?)s.Attribute("Value") == "3,0,0,0");
        baseSetters.Should().Contain(s =>
            (string?)s.Attribute("Property") == "BorderBrush" &&
            (string?)s.Attribute("Value") == "Transparent");

        // IsError 触发器把左条染红。
        var errorTrigger = style.Descendants(P + "DataTrigger")
            .Single(t => (string?)t.Attribute("Binding") == "{Binding IsError}");
        errorTrigger.Elements(P + "Setter").Should().Contain(s =>
            (string?)s.Attribute("Property") == "BorderBrush" &&
            (string?)s.Attribute("Value") == "{StaticResource Error}");

        // 其余触发器不得改边框：改 BorderBrush 会覆盖红条，改 BorderThickness 会造成行内容位移。
        foreach (var trigger in style.Descendants(P + "DataTrigger"))
        {
            var isError = (string?)trigger.Attribute("Binding") == "{Binding IsError}";
            var setters = trigger.Elements(P + "Setter").ToList();
            setters.Should().NotContain(s => (string?)s.Attribute("Property") == "BorderThickness");
            if (!isError)
                setters.Should().NotContain(s => (string?)s.Attribute("Property") == "BorderBrush");
        }
    }

    [Fact]
    public void Highlight_Triggers_Never_Touch_BorderBrush()
    {
        var doc = LoadXaml();
        var grid = doc.Descendants(P + "DataGrid")
            .Single(e => (string?)e.Attribute(X + "Name") == "TraceGrid");
        var highlightTriggers = grid.Element(P + "DataGrid.RowStyle")!
            .Descendants(P + "DataTrigger")
            .Where(t => ((string?)t.Attribute("Binding"))?.Contains("HighlightColorIndex") == true)
            .ToList();

        highlightTriggers.Should().HaveCountGreaterThan(0);
        foreach (var trigger in highlightTriggers)
        {
            trigger.Elements(P + "Setter").Should().OnlyContain(s =>
                (string?)s.Attribute("Property") == "Background");
        }
    }

    [Fact]
    public void TraceGrid_Has_RowContextMenu_And_RightClickSelection()
    {
        var doc = LoadXaml();
        var grid = doc.Descendants(P + "DataGrid")
            .Single(e => (string?)e.Attribute(X + "Name") == "TraceGrid");

        ((string?)grid.Attribute("PreviewMouseRightButtonDown"))
            .Should().Be("OnGridPreviewMouseRightButtonDown");
        ((string?)grid.Attribute("ContextMenuOpening"))
            .Should().Be("OnGridContextMenuOpening");

        var menu = grid.Element(P + "DataGrid.ContextMenu")?.Element(P + "ContextMenu");
        menu.Should().NotBeNull("右键菜单必须挂在主 TraceGrid 上");

        var items = menu!.Elements(P + "MenuItem").ToList();
        items.Should().HaveCount(4);

        var commandItems = items.Where(i => i.Attribute("Command") is not null).ToList();
        commandItems.Should().HaveCount(3);
        commandItems.Select(i => (string?)i.Attribute("Command")).Should().Contain(c =>
            c!.Contains("ShowOnlyThisIdCommand"));
        commandItems.Select(i => (string?)i.Attribute("Command")).Should().Contain(c =>
            c!.Contains("ExcludeThisIdCommand"));
        commandItems.Select(i => (string?)i.Attribute("Command")).Should().Contain(c =>
            c!.Contains("HighlightThisIdCommand"));

        // ContextMenu 不在 DataGrid 可视树内：命令与参数必须经 PlacementTarget。
        commandItems.Should().OnlyContain(i =>
            ((string?)i.Attribute("Command"))!.Contains("PlacementTarget.DataContext"));
        commandItems.Should().OnlyContain(i =>
            ((string?)i.Attribute("CommandParameter"))!.Contains("PlacementTarget.SelectedItem"));

        items.Should().Contain(i => (string?)i.Attribute("Click") == "OnCopyIdAndData");

        // 排除集合的逆操作入口必须存在于工具栏（否则排除后无法在界面内恢复）。
        var hasClearExcludedButton = doc.Descendants(P + "Button").Any(b =>
            ((string?)b.Attribute("Command"))?.Contains("ClearExcludedIdsCommand") == true);
        hasClearExcludedButton.Should().BeTrue();
    }

    [Fact]
    public void Filter_Builder_And_Advanced_Expander_Contract()
    {
        var doc = LoadXaml();

        // 高级过滤：默认折叠，且收纳了全部批量输入字段。
        var advanced = doc.Descendants(P + "Expander")
            .Single(e => (string?)e.Attribute(X + "Name") == "AdvancedFilterExpander");
        ((string?)advanced.Attribute("IsExpanded")).Should().Be("False", "默认折叠，日常入口是条件条");
        var advancedTextBoxes = advanced.Descendants(P + "TextBox")
            .Select(t => (string?)t.Attribute("Text")).Where(t => t != null).ToList();
        foreach (var field in new[] { "IdListText", "PgnText", "SaText", "DaText",
                                      "PayloadOffsetText", "PayloadMaskHex", "PayloadValueHex" })
            advancedTextBoxes.Should().Contain(t => t!.Contains(field), $"{field} 应留在高级区");
        advanced.Descendants(P + "ComboBox").Select(c => (string?)c.Attribute("Text"))
            .Should().Contain(t => t != null && t.Contains("DbcMessageName"));
        advanced.Descendants(P + "ComboBox").Select(c => (string?)c.Attribute("SelectedValue"))
            .Should().Contain(t => t != null && t.Contains("ChannelFilter"));
        advanced.Descendants(P + "CheckBox").Select(c => (string?)c.Attribute("IsChecked"))
            .Should().Contain(t => t != null && t.Contains("ExcludeMatch"));

        // 高频开关必须留在常驻工具栏：折叠高级区后仍可一键操作。
        foreach (var binding in new[] { "IsPaused", "ShowErrorsOnly" })
        {
            advanced.Descendants(P + "CheckBox").Select(c => (string?)c.Attribute("IsChecked"))
                .Should().NotContain(t => t != null && t.Contains(binding), $"{binding} 不能被折进高级区");
            doc.Descendants(P + "CheckBox").Select(c => (string?)c.Attribute("IsChecked"))
                .Should().Contain(t => t != null && t.Contains(binding));
        }

        // 「+ 条件」按钮 + 弹层：弹层绑 IsBuilderOpen，内部有字段选择与添加命令。
        var addButton = doc.Descendants(P + "Button")
            .Single(b => (string?)b.Attribute("Content") == "+ 条件");
        // 只做"打开"不做 toggle：Toggle 与 StaysOpen=False 会互抢鼠标捕获，按钮会关不掉。
        ((string?)addButton.Attribute("Command"))!.Should().Contain("OpenFilterBuilderCommand");

        var popup = doc.Descendants(P + "Popup").Single();
        var isOpen = (string?)popup.Attribute("IsOpen");
        isOpen.Should().NotBeNull();
        isOpen!.Should().Contain("IsBuilderOpen");
        // 外部点击关闭后必须把 VM 也写回 false，否则下次点击会把 false 再翻成 false（按钮永远打不开）。
        isOpen.Should().Contain("Mode=TwoWay");
        ((string?)popup.Attribute("StaysOpen")).Should().Be("False", "点弹层外部应关闭");
        popup.Descendants(P + "Button").Select(b => (string?)b.Attribute("Command"))
            .Should().Contain(t => t != null && t.Contains("CloseFilterBuilderCommand"), "弹层要有显式关闭入口");
        ((string?)popup.Attribute("PlacementTarget"))!.Should().Contain("AddConditionButton");
        popup.Descendants(P + "ComboBox").Select(c => (string?)c.Attribute("ItemsSource"))
            .Should().Contain(t => t != null && t.Contains("FilterBuilderFields"));
        popup.Descendants(P + "ComboBox").Select(c => (string?)c.Attribute("SelectedItem"))
            .Should().Contain(t => t != null && t.Contains("SelectedBuilderField"));
        popup.Descendants(P + "Button").Select(b => (string?)b.Attribute("Command"))
            .Should().Contain(t => t != null && t.Contains("AddFilterConditionCommand"));
        popup.Descendants(P + "TextBlock").Select(t => (string?)t.Attribute("Text"))
            .Should().Contain(t => t != null && t.Contains("BuilderError"));
    }

    [Fact]
    public void Filter_Chip_Bar_Binds_To_FilterChips()
    {
        var doc = LoadXaml();
        var chipHost = doc.Descendants(P + "ItemsControl")
            .FirstOrDefault(ic => (string?)ic.Attribute("ItemsSource") == "{Binding FilterChips}");
        chipHost.Should().NotBeNull("过滤 chip 条必须绑定 FilterChips");
        chipHost!.Descendants(P + "Button").Should().Contain(b =>
            (string?)b.Attribute("Command") == "{Binding RemoveCommand}");
    }

    [Fact]
    public void Filter_Chip_Bar_Keeps_Wrap_EmptyState_And_Error_Contracts()
    {
        var doc = LoadXaml();
        var chipHost = doc.Descendants(P + "ItemsControl")
            .Single(ic => (string?)ic.Attribute("ItemsSource") == "{Binding FilterChips}");

        // WrapPanel 才会折行（换成水平 StackPanel 就永不折行）。
        chipHost.Element(P + "ItemsControl.ItemsPanel")
            ?.Descendants(P + "WrapPanel").Any().Should().BeTrue();

        // ItemsControl 必须处在 DockPanel 的最后一个子元素，LastChildFill 才给有限宽度。
        var dock = chipHost.Ancestors(P + "DockPanel").First();
        var scroll = chipHost.Ancestors(P + "ScrollViewer").First();
        dock.Elements().Last().Should().Be(scroll, "WrapPanel 的容器必须吃剩余宽度");

        // 空态提示绑定 FilterChips.Count == 0。
        var hasEmptyHint = doc.Descendants(P + "TextBlock").Any(t =>
            ((string?)t.Attribute("Text"))?.Contains("无过滤条件") == true &&
            t.Descendants(P + "DataTrigger").Any(d =>
                (string?)d.Attribute("Binding") == "{Binding FilterChips.Count}"));
        hasEmptyHint.Should().BeTrue();

        // 非法输入态提示（chip 未生效）必须存在。
        doc.Descendants(P + "TextBlock").Any(t =>
            ((string?)t.Attribute("Text"))?.Contains("过滤未生效") == true).Should().BeTrue();
    }

    [Fact]
    public void TraceGrid_Has_Two_Empty_States_With_Clear_Filters_Action()
    {
        var doc = LoadXaml();
        var grid = doc.Descendants(P + "DataGrid")
            .Single(e => (string?)e.Attribute(X + "Name") == "TraceGrid");
        var host = grid.Parent!;

        host.Name.LocalName.Should().Be("Grid", "空态面板必须与表格同格叠放");
        var panels = host.Elements().Where(e => e.Name.LocalName == "EmptyStatePanel").ToList();
        panels.Should().HaveCount(2, "无数据 / 被过滤掉 是两种不同空态");
        host.Elements().Last().Name.LocalName.Should().Be("EmptyStatePanel",
            "面板必须排在表格之后，否则被表格盖住");

        // 空态一：一帧都没有 —— 只能靠连接通道，没有可在本页执行的逆操作。
        var noData = panels.Single(p => ((string?)p.Attribute("Title"))?.Contains("还没有收到帧") == true);
        noData.Attribute("ActionCommand").Should().BeNull();
        ((string?)noData.Attribute("Description"))!.Should().Contain("连接");

        // 空态二：有帧但被过滤条件全部排除 —— 必须给"清除过滤"这条出路。
        var filteredOut = panels.Single(p => ((string?)p.Attribute("Title"))?.Contains("过滤") == true);
        ((string?)filteredOut.Attribute("ActionText")).Should().Be("清除过滤");
        ((string?)filteredOut.Attribute("ActionCommand"))!.Should().Contain("ClearFiltersCommand");

        // 触发条件走 VM 布尔值（Style 里的 ElementName 解析不可靠，失效时面板会静默不出现）。
        // 属性元素挂在控件自身的 xmlns 下（controls:EmptyStatePanel.Style），按 LocalName 取。
        var style = filteredOut.Elements()
            .SingleOrDefault(e => e.Name.LocalName == "EmptyStatePanel.Style")?
            .Element(P + "Style");
        style.Should().NotBeNull();
        style!.Descendants(P + "DataTrigger").Should().Contain(t =>
            (string?)t.Attribute("Binding") == "{Binding IsTraceFilteredEmpty}" &&
            (string?)t.Attribute("Value") == "True");
    }

    [Fact]
    public void CopyText_Formats_Id_And_Data_Without_Trailing_Space()
    {
        var withData = new TraceEntry
        {
            Id = new CanId(0x1A, FrameFormat.Standard),
            DataHex = "DE AD",
        };
        TraceView.BuildIdAndData(withData).Should().Be("0x01A DE AD");

        var emptyData = new TraceEntry
        {
            Id = new CanId(0x1A, FrameFormat.Standard),
            DataHex = "",
        };
        TraceView.BuildIdAndData(emptyData).Should().Be("0x01A");
    }
}
