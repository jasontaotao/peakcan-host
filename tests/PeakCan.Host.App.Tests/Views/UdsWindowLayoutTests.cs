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

    private static readonly string[] DiagnosticTabs = { "DID", "例程", "DTC" };

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
            .Single(t => (string?)t.Attribute("Header") == "刷写");

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

    /// <summary>
    /// 例程表格「状态」列必须绑中文 StatusDisplay；绑 Status 会把
    /// Running/Completed/Failed 这些内部状态直接渲染给用户。
    /// </summary>
    [Fact]
    public void Routines_Status_Column_Binds_StatusDisplay()
    {
        var tab = UdsWindow().Descendants(P + "TabItem")
            .Single(t => (string?)t.Attribute("Header") == "例程");
        var statusColumn = tab.Descendants(P + "DataGridTextColumn")
            .SingleOrDefault(c => (string?)c.Attribute("Header") == "状态");

        statusColumn.Should().NotBeNull("例程表必须有状态列");
        ((string?)statusColumn!.Attribute("Binding")).Should()
            .Contain("StatusDisplay", "状态列显示中文；绑 Status 会把英文状态漏给用户");
    }

    /// <summary>
    /// 界面文案中文化：刷写页的按钮/空态必须是中文。
    /// 这些英文串曾经直接出现在 operator 面前，回归时必须钉住。
    /// </summary>
    [Theory]
    [InlineData("添加固件文件")]
    [InlineData("添加刷写驱动")]
    [InlineData("（未选中）")]
    [InlineData("（未加载驱动）")]
    [InlineData("（此步骤类型无可配置属性）")]
    public void Flash_Labels_Are_Chinese(string label)
    {
        XDocument.Load(RepoFile("src", "PeakCan.Host.App", "Windows", "UdsWindow.xaml"))
            .ToString()
            .Should().Contain(label, $"刷写页文案应中文化，不能残留英文 {label}");
    }

    [Theory]
    [InlineData("+ Add Firmware File")]
    [InlineData("- Remove")]
    [InlineData("(No configurable properties for this step kind)")]
    [InlineData("(none selected)")]
    [InlineData("(no driver loaded)")]
    public void Flash_English_Remnants_Are_Gone(string remnant)
    {
        XDocument.Load(RepoFile("src", "PeakCan.Host.App", "Windows", "UdsWindow.xaml"))
            .ToString()
            .Should().NotContain(remnant, $"旧英文文案必须清理：{remnant}");
    }

    /// <summary>
    /// <b>页签索引守卫。</b>
    /// <para>
    /// <c>UdsViewModel.LogAnalysisTabIndex</c> 是硬编码数字，<c>IsOnlineTab</c> 全靠它。
    /// 加/删/挪页签时它不会报错——只会静默变成「离线页仍显示在线控件」，
    /// 或者在离线页发诊断请求。这条测试按 XAML 里的<b>实际顺序</b>核对常量，
    /// 移位必然变红，逼着改的人同步更新。
    /// </para>
    /// </summary>
    [Fact]
    public void Tab_index_constants_match_the_actual_tab_order()
    {
        var headers = UdsWindow().Descendants(P + "TabControl").Single()
            .Elements(P + "TabItem")
            .Select(t => (string?)t.Attribute("Header"))
            .ToList();

        headers.Should().NotBeEmpty();
        UdsViewModel.LogAnalysisTabIndex.Should().BeLessThan(headers.Count,
            "日志分析页签索引超出页签数量 = IsOnlineTab 永远算错");
        headers[UdsViewModel.LogAnalysisTabIndex].Should()
            .Be("日志分析", "LogAnalysisTabIndex 必须指向日志分析页签");

        UdsViewModel.TransportParamsTabIndex.Should().BeLessThan(headers.Count);
        headers[UdsViewModel.TransportParamsTabIndex].Should()
            .Be("通信参数", "TransportParamsTabIndex 必须指向通信参数页签");
    }

    /// <summary>
    /// 通信参数不能回到顶部 Expander：展开时它会把整个 TabControl 压扁。
    /// </summary>
    [Fact]
    public void Transport_params_is_not_a_top_docked_expander()
    {
        var topExpanders = UdsWindow().Descendants(P + "Expander")
            .Where(e => ((string?)e.Attribute("DockPanel.Dock")) == "Top")
            .Select(e => (string?)e.Attribute("Header"))
            .ToList();

        topExpanders.Should().NotContain("通信参数",
            "顶栏 Expander 展开后会压扁主区域；它必须是独立页签");
    }
}
