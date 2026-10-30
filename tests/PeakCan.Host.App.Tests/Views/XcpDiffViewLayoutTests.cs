using System.IO;
using System.Linq;
using System.Xml.Linq;
using FluentAssertions;

namespace PeakCan.Host.App.Tests.Views;

/// <summary>
/// S6-T5 参数集 diff 页布局回归。曾出现星号列错位：目标路径输入框落进 Auto 列
/// （被压成一条缝），「浏览...」按钮落进 <c>*</c> 列（吞掉剩余宽度拉成整行宽）。
/// 本测试按 XML 结构钉死不变式——星号列只给路径输入框，浏览按钮不得占星号列。
/// 只读 XAML 文本，不触碰 WPF 运行时（与 SendViewTests 同一先例）。
/// </summary>
public class XcpDiffViewLayoutTests
{
    private static readonly XNamespace P =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static XElement LoadDiffRow()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "PeakCan.Host.App", "Views", "Xcp", "XcpDiffView.xaml"));
        var doc = XDocument.Load(path);
        return doc.Descendants(P + "Grid")
            .Single(g => g.Elements(P + "Button")
                .Any(b => (string?)b.Attribute("Click") == "OnBrowseTarget"));
    }

    private static string[] ColumnWidths(XElement grid) =>
        grid.Element(P + "Grid.ColumnDefinitions")!.Elements(P + "ColumnDefinition")
            .Select(c => (string?)c.Attribute("Width") ?? "Auto")
            .ToArray();

    // 附加属性在 XAML 里写成无前缀的 Grid.Column，故按无命名空间属性取；
    // 而 Width 是 ColumnDefinition 的普通属性，同样无前缀。
    private static int ColumnOf(XElement child) =>
        int.TryParse((string?)child.Attribute("Grid.Column"), out var c) ? c : 0;

    [Theory]
    [InlineData("BaselinePath")]
    [InlineData("TargetPath")]
    public void PathTextBox_OccupiesStarColumn(string bindingPath)
    {
        var grid = LoadDiffRow();
        var widths = ColumnWidths(grid);
        var box = grid.Elements(P + "TextBox")
            .Single(t => ((string?)t.Attribute("Text"))?.Contains(bindingPath) == true);

        widths[ColumnOf(box)].Should().Be("*",
            $"{bindingPath} 输入框必须占星号列，否则会被 Auto 列压成一条缝");
    }

    [Theory]
    [InlineData("OnBrowseBaseline")]
    [InlineData("OnBrowseTarget")]
    public void BrowseButton_DoesNotOccupyStarColumn(string click)
    {
        var grid = LoadDiffRow();
        var widths = ColumnWidths(grid);
        var button = grid.Elements(P + "Button")
            .Single(b => (string?)b.Attribute("Click") == click);

        widths[ColumnOf(button)].Should().NotBe("*",
            $"{click} 按钮不得占星号列——按钮落在星号列会被拉伸成整行宽");
    }
}
