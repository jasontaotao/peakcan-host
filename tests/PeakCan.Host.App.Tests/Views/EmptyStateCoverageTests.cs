using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Input;
using System.Xml.Linq;
using FluentAssertions;
using PeakCan.Host.App.ViewModels;
using Xunit;

namespace PeakCan.Host.App.Tests.Views;

/// <summary>
/// 剩余空表格的空态覆盖（P0 空态改造的收尾）。
/// <para>
/// 两个都必须由测试钉住的地方：
/// 1. 面板要**叠在表格之后**（同一父容器、Z 序在后）——放在表格之前会被表格盖住，
///    面板存在但用户永远看不见；
/// 2. <see cref="System.Windows.Data.DataTrigger"/> 的绑定路径要真实存在。WPF 绑定失败
///    是静默的：写错一个字母面板就永远不显示，而 XAML 看起来"做好了"。
/// </para>
/// </summary>
public sealed class EmptyStateCoverageTests
{
    /// <param name="View">Views 目录下的文件名。</param>
    /// <param name="Title">空态面板的 Title（唯一标识）。</param>
    /// <param name="Binding">DataTrigger 的绑定路径（不含大括号）。</param>
    /// <param name="Value">DataTrigger 的期望值（布尔空态用 True，计数空态用 0）。</param>
    /// <param name="OwnerType">该绑定的真实 DataContext 类型（面板继承宿主 VM）。</param>
    private sealed record Expectation(
        string View, string Title, string Binding, string Value, Type OwnerType);

    private static readonly Expectation[] Expected =
    {
        new("DbcView.xaml",    "还没有加载 DBC",        "HasNoMessages",            "True", typeof(DbcViewModel)),
        new("DbcView.xaml",    "搜索没有匹配的消息",    "IsSearchEmpty",            "True", typeof(DbcViewModel)),
        new("SendView.xaml",   "帧库还是空的",          "Library.Count",            "0",    typeof(SendViewModel)),
        // 信号表在 <StackPanel DataContext="{Binding DbcSend}"> 里，宿主不是 SendViewModel。
        new("SendView.xaml",   "先选一条 DBC 消息",     "SignalRows.Count",         "0",    typeof(DbcSendViewModel)),
        new("ReplayView.xaml", "还没有书签",            "Bookmarks.Count",          "0",    typeof(ReplayViewModel)),
        new("TraceView.xaml",  "还没有 ID 统计",        "StatsRows.Count",          "0",    typeof(TraceViewModel)),
        new("TraceView.xaml",  "还没有 J1939 会话事件", "J1939SessionEvents.Count", "0",    typeof(TraceViewModel)),
        new("HilView.xaml",    "还没有试运行诊断",      "TrialDiagnostics.Count",   "0",    typeof(HilViewModel)),
        new("HilView.xaml",    "还没有运行记录",        "RunHistory.Count",         "0",    typeof(HilViewModel)),
    };

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

    private static XDocument LoadView(string view)
        => XDocument.Load(RepoFile("src", "PeakCan.Host.App", "Views", view));

    private static XElement Panel(XDocument doc, string title)
        => doc.Descendants()
              .SingleOrDefault(e => e.Name.LocalName == "EmptyStatePanel"
                                    && (string?)e.Attribute("Title") == title)!;

    [Fact]
    public void Every_Remaining_Empty_Table_Has_An_Empty_State()
    {
        foreach (var exp in Expected)
        {
            var panel = Panel(LoadView(exp.View), exp.Title);
            panel.Should().NotBeNull($"{exp.View} 缺了「{exp.Title}」空态");

            var trigger = panel!.Descendants()
                .SingleOrDefault(e => e.Name.LocalName == "DataTrigger");
            trigger.Should().NotBeNull($"「{exp.Title}」必须由 DataTrigger 控制显隐");
            ((string?)trigger!.Attribute("Binding")).Should()
                .Be($"{{Binding {exp.Binding}}}", $"「{exp.Title}」的判据写错了（绑定失败是静默的）");
            ((string?)trigger.Attribute("Value")).Should().Be(exp.Value);
        }
    }

    [Fact]
    public void Empty_State_Panels_Are_Declared_After_Their_Table()
    {
        // 同一父容器里，面板必须排在表格之后（后声明的子元素画在上层）。
        foreach (var exp in Expected)
        {
            var panel = Panel(LoadView(exp.View), exp.Title);
            panel.Should().NotBeNull();

            var parent = panel!.Parent!;
            var children = parent.Elements().ToList();
            var panelIndex = children.IndexOf(panel);
            var gridIndex = children.FindIndex(e => e.Name.LocalName == "DataGrid");

            gridIndex.Should().BeGreaterThanOrEqualTo(0,
                $"「{exp.Title}」应当和表格叠在同一个 Grid 里（找不到 DataGrid 说明结构变了）");
            panelIndex.Should().BeGreaterThan(gridIndex,
                $"「{exp.Title}」要声明在表格之后，否则会被表格盖住");
        }
    }

    [Fact]
    public void Every_Trigger_Binding_Exists_On_Its_Real_DataContext()
    {
        foreach (var exp in Expected)
        {
            var root = exp.Binding.Split('.')[0];

            exp.OwnerType.GetProperty(root, BindingFlags.Public | BindingFlags.Instance)
               .Should().NotBeNull(
                   $"{exp.View} 的「{exp.Title}」绑的是 {exp.Binding}，但 {exp.OwnerType.Name} 上没有 {root}"
                   + "（WPF 绑定失败是静默的，面板会永远不显示）");
        }
    }

    [Fact]
    public void Dbc_Empty_State_Bindings_Exist_On_The_ViewModel()
    {
        // 面板上的两个判据 + 清除搜索命令都要真实存在。
        typeof(DbcViewModel).GetProperty(nameof(DbcViewModel.HasNoMessages), BindingFlags.Public | BindingFlags.Instance)
            .Should().NotBeNull();
        typeof(DbcViewModel).GetProperty(nameof(DbcViewModel.IsSearchEmpty), BindingFlags.Public | BindingFlags.Instance)
            .Should().NotBeNull();

        var command = typeof(DbcViewModel).GetProperty("ClearSearchCommand", BindingFlags.Public | BindingFlags.Instance);
        command.Should().NotBeNull("「清除搜索」按钮绑的是 ClearSearchCommand，缺了就点不动");
        typeof(ICommand).IsAssignableFrom(command!.PropertyType).Should().BeTrue();
    }

    [Fact]
    public void Dbc_Search_Empty_State_Uses_The_Clear_Search_Command()
    {
        var panel = Panel(LoadView("DbcView.xaml"), "搜索没有匹配的消息");

        ((string?)panel.Attribute("ActionCommand")).Should().Be("{Binding ClearSearchCommand}");
    }
}
