using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using FluentAssertions;
using PeakCan.Host.App.ViewModels.Uds;
using Xunit;

namespace PeakCan.Host.App.Tests.Views;

/// <summary>
/// P1 防事故：刷写期间锁诊断面板的**声明层**契约。
/// <para>
/// 锁定全靠 XAML 上的 <c>IsEnabled="{Binding IsDiagnosticsEnabled}"</c>。
/// 只要有人删掉一行、或者把绑定名写错（WPF 静默失败），面板就又能点了——
/// 而这正好是"刷写中被诊断请求抢走 ECU"的入口。所以这里逐个 TabItem 查。
/// </para>
/// </summary>
public sealed class UdsWindowLayoutTests
{
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string[] DiagnosticTabs = { "DIDs", "Routines", "DTCs" };

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

    private static XDocument UdsWindow()
        => XDocument.Load(RepoFile("src", "PeakCan.Host.App", "Windows", "UdsWindow.xaml"));

    [Fact]
    public void File_Still_Documents_A_Real_Lock_Property()
    {
        // 绑定名写错时 WPF 不报错，只会永远保持启用态。
        typeof(UdsViewModel).GetProperty(nameof(UdsViewModel.IsDiagnosticsEnabled))
            .Should().NotBeNull("XAML 绑的是 IsDiagnosticsEnabled，属性一旦改名绑定就静默失效");
    }

    [Fact]
    public void Diagnostic_Tabs_Are_Locked_During_Flash()
    {
        var doc = UdsWindow();

        foreach (var header in DiagnosticTabs)
        {
            var tab = doc.Descendants(P + "TabItem")
                .SingleOrDefault(t => (string?)t.Attribute("Header") == header);
            tab.Should().NotBeNull($"找不到 {header} tab");
            ((string?)tab!.Attribute("IsEnabled")).Should()
                .Be("{Binding IsDiagnosticsEnabled}", $"{header} 在刷写期间必须锁死");
        }
    }

    [Fact]
    public void Flashing_Tab_Stays_Usable()
    {
        var tab = UdsWindow().Descendants(P + "TabItem")
            .Single(t => (string?)t.Attribute("Header") == "Flashing");

        tab.Attribute("IsEnabled").Should().BeNull("刷写页自己当然不能锁，否则没法停止刷写");
    }

    [Fact]
    public void Session_Strip_Is_Locked_During_Flash()
    {
        var strip = UdsWindow().Descendants(P + "Border")
            .SingleOrDefault(b => ((string?)b.Attribute("DockPanel.Dock")) == "Top"
                                  && ((string?)b.Attribute("IsEnabled"))?.Contains("IsDiagnosticsEnabled") == true);

        strip.Should().NotBeNull("顶部会话条（切会话 / SecurityAccess / TesterPresent）同样会抢 ECU");
    }

    [Fact]
    public void Flash_Banner_Is_Driven_By_IsFlashing()
    {
        var banner = UdsWindow().Descendants(P + "Border")
            .SingleOrDefault(b => ((string?)b.Attribute("Visibility"))?
                .Contains("Flash.IsFlashing", StringComparison.Ordinal) == true);

        banner.Should().NotBeNull("只禁用不说明，用户会以为界面卡住了");
        banner!.Descendants(P + "TextBlock").Should().NotBeEmpty();
    }
}
