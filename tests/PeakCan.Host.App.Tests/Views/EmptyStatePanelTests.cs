using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace PeakCan.Host.App.Tests.Views;

/// <summary>
/// P0 空态面板的契约：
/// 1) 四个依赖属性（Title / Description / ActionText / ActionCommand）都必须是 public static DP，
///    宿主视图才能用属性语法赋值；
/// 2) 面板内部只能经 RelativeSource 回指自身（面板的 DataContext 是宿主 VM，不是面板本身）；
/// 3) 没有 ActionCommand 时按钮必须整块消失（不是留一个禁用按钮）。
/// <para>
/// 面板的运行时加载由 <c>AppShellViewModelTests.ShowSignalsCommand_Reuses_Cached_View_Instance</c>
/// 间接覆盖（那个 STA 用例构造 SignalView，SignalView 内含本面板）；在这里再写一个
/// 构造控件 + 泵 dispatcher 的 STA 用例会把整套测试拖死（本仓实测过），所以本文件只做结构断言。
/// </para>
/// </summary>
public sealed class EmptyStatePanelTests
{
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static string ResolvePanelPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PeakCan.Host.slnx")))
            dir = dir.Parent!;
        return Path.Combine(dir!.FullName, "src", "PeakCan.Host.App", "Views", "Controls", "EmptyStatePanel.xaml");
    }

    [Fact]
    public void Panel_Exposes_Content_Dependency_Properties()
    {
        var type = typeof(PeakCan.Host.App.Views.Controls.EmptyStatePanel);

        foreach (var name in new[] { "Title", "Description", "ActionText", "ActionCommand" })
        {
            var field = type.GetField($"{name}Property",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            field.Should().NotBeNull($"{name}Property 必须是 public static DP，否则 XAML 属性语法失效");
        }
    }

    [Fact]
    public void Panel_Binds_To_Its_Own_Properties_Via_RelativeSource()
    {
        var doc = XDocument.Load(ResolvePanelPath());

        var texts = doc.Descendants(P + "TextBlock")
            .Select(t => (string?)t.Attribute("Text"))
            .Where(t => t is not null)
            .ToList();

        texts.Should().Contain(t => t!.Contains("Title") && t.Contains("AncestorType=UserControl"),
            "Title 必须从 UserControl 自身取，不能从宿主 VM 取");
        texts.Should().Contain(t => t!.Contains("Description") && t.Contains("AncestorType=UserControl"));
    }

    [Fact]
    public void Panel_Hides_Action_Button_When_No_Command()
    {
        var doc = XDocument.Load(ResolvePanelPath());

        var button = doc.Descendants(P + "Button").Single();

        ((string?)button.Attribute("Content"))!.Should().Contain("ActionText");
        ((string?)button.Attribute("Content"))!.Should().Contain("AncestorType=UserControl");
        ((string?)button.Attribute("Command"))!.Should().Contain("ActionCommand");

        var visibility = (string?)button.Attribute("Visibility");
        visibility.Should().NotBeNull("按钮可见性必须由 ActionCommand 是否为空驱动");
        visibility!.Should().Contain("ActionCommand");
        visibility.Should().Contain("NullToVisibilityConverter");
    }
}
