using System.IO;
using System.Linq;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace PeakCan.Host.App.Tests.Views;

/// <summary>
/// 声明层守卫：日志分析页签/在线控件显隐/分析页命令绑定。绑定路径写错时
/// WPF 静默失败，按钮点了没反应——只能靠 XAML 断言兜住。
/// </summary>
public sealed class UdsLogAnalysisViewLayoutTests
{
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static string RepoFile(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PeakCan.Host.slnx")))
            dir = dir.Parent;

        if (dir is null)
            throw new FileNotFoundException(
                $"找不到 PeakCan.Host.slnx —— 测试是从非仓库目录运行的？起始目录: {AppContext.BaseDirectory}");

        return Path.Combine(new[] { dir.FullName }.Concat(relative).ToArray());
    }

    private static XDocument LoadXaml(params string[] relative)
        => XDocument.Load(RepoFile(relative));

    [Fact]
    public void Window_has_log_analysis_tab_bound_to_child_view_model()
    {
        var doc = LoadXaml("src", "PeakCan.Host.App", "Windows", "UdsWindow.xaml");
        var tabs = doc.Descendants(P + "TabItem").ToList();
        tabs.Should().Contain(t => (string?)t.Attribute("Header") == "日志分析");

        var tab = tabs.Single(t => (string?)t.Attribute("Header") == "日志分析");
        tab.Descendants().Should().Contain(e =>
            (((string?)e.Attribute("DataContext")) ?? "").Contains("LogAnalysis", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Online_controls_are_hidden_when_log_analysis_tab_selected()
    {
        var doc = LoadXaml("src", "PeakCan.Host.App", "Windows", "UdsWindow.xaml");
        doc.Descendants(P + "Button").Should().Contain(b =>
            (string?)b.Attribute("Content") == "默认" &&
            (((string?)b.Attribute("Visibility")) ?? "").Contains("IsOnlineTab", System.StringComparison.Ordinal));

        // ODX 按钮不绑定在线页签显隐（加载 ODX 在离线页仍可用）
        var odx = doc.Descendants(P + "Button").Single(b => (string?)b.Attribute("Content") == "加载 ODX…");
        (((string?)odx.Attribute("Visibility")) ?? "").Should().NotContain("IsOnlineTab");
    }

    [Fact]
    public void Analysis_view_exposes_required_commands()
    {
        var doc = LoadXaml("src", "PeakCan.Host.App", "Views", "UdsLogAnalysisView.xaml");
        var commands = doc.Descendants(P + "Button").Select(b => (string?)b.Attribute("Command")).ToList();

        commands.Should().Contain("{Binding OpenLogCommand}");
        commands.Should().Contain("{Binding AnalyzeCommand}");
        commands.Should().Contain(c => c != null && c.Contains("Export"));
    }

    [Fact]
    public void Analysis_view_shows_interpretation_degradation_counts_and_odx_state()
    {
        var doc = LoadXaml("src", "PeakCan.Host.App", "Views", "UdsLogAnalysisView.xaml");
        var bindings = doc.Descendants()
            .Select(e => (string?)e.Attribute("Text") ?? (string?)e.Attribute("Binding") ?? (string?)e.Attribute("ItemsSource"))
            .Where(v => v != null)
            .ToList();

        bindings.Should().Contain(b => b!.Contains("Interpretation.DidName"));
        bindings.Should().Contain(b => b!.Contains("Interpretation.DecodedValue"));
        bindings.Should().Contain(b => b!.Contains("Interpretation.DegradeReason"));
        bindings.Should().Contain(b => b!.Contains("Noise.TopIds") || b!.Contains("Noise.Reasons"));
        bindings.Should().Contain(b => b!.Contains("IsOdxLoaded"));
        bindings.Should().Contain(b => b!.Contains("FilterEmptyHint"));
    }
}
