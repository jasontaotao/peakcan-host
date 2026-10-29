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
