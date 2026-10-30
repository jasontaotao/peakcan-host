using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace PeakCan.Host.App.Tests.Views;

/// <summary>
/// Replay 时间轴热力条的布局契约：绑定 TimelineHeat、用 UniformGrid 均分、
/// 错误格标红、点击格触发 SeekToCommand。
/// </summary>
public sealed class ReplayViewLayoutTests
{
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static string ResolveReplayViewPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PeakCan.Host.slnx")))
            dir = dir.Parent!;
        return Path.Combine(dir!.FullName, "src", "PeakCan.Host.App", "Views", "ReplayView.xaml");
    }

    [Fact]
    public void Heat_Bar_Binds_TimelineHeat_With_UniformGrid_And_Error_Rule()
    {
        var doc = XDocument.Load(ResolveReplayViewPath());

        var host = doc.Descendants(P + "ItemsControl")
            .Single(ic => (string?)ic.Attribute("ItemsSource") == "{Binding TimelineHeat}");

        // 必须与拖条同列（跨列会让时间轴与播放头错位）。
        ((string?)host.Attribute("Grid.Column")).Should().Be("0");
        host.Attribute("Grid.ColumnSpan").Should().BeNull();

        // 固定格数靠 UniformGrid 均分宽度。
        var panel = host.Element(P + "ItemsControl.ItemsPanel");
        panel.Should().NotBeNull();
        panel!.Descendants(P + "UniformGrid").Any().Should().BeTrue();

        // 密度编码：Opacity 绑 Intensity。
        host.Descendants(P + "Setter").Should().Contain(s =>
            (string?)s.Attribute("Property") == "Opacity" &&
            (string?)s.Attribute("Value") == "{Binding Intensity}");

        // 错误格整格标红。
        host.Descendants(P + "DataTrigger").Any(t =>
            (string?)t.Attribute("Binding") == "{Binding HasError}" &&
            t.Elements(P + "Setter").Any(s =>
                (string?)s.Attribute("Property") == "Background" &&
                (string?)s.Attribute("Value") == "{StaticResource Error}")).Should().BeTrue();

        // 空集合隐藏（不留 16px 空白带）。
        host.Descendants(P + "DataTrigger").Any(t =>
            (string?)t.Attribute("Binding") == "{Binding TimelineHeat.Count}").Should().BeTrue();

        // 点击格跳转到该时间段起点；命令必须从 UserControl 的 DataContext 解析。
        var mouse = host.Descendants(P + "MouseBinding").Single();
        ((string?)mouse.Attribute("CommandParameter")).Should().Be("{Binding Start}");
        var command = Regex.Replace(
            (string?)mouse.Attribute("Command") ?? "", @"\s+", "");
        command.Should().Contain("SeekToCommand");
        command.Should().Contain("AncestorType=UserControl");
    }
}
