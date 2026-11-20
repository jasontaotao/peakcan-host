using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using PeakCan.Host.App.Tests.Collections;

namespace PeakCan.Host.App.Tests.Views;

/// <summary>
/// NM 面板与补全对话框的加载期守卫：<c>{StaticResource}</c> 引用的 key 必须在本文件
/// 或 App.xaml 里定义。缺 key 是控件实例化时的 <c>XamlParseException</c>，不是运行时
/// 警告；这条曾在补全对话框上踩过一次（转换器晚一个任务才创建）。
/// </summary>
[Collection(WpfAppTestCollection.Name)]
public class NmViewXamlResourceTests
{
    private static readonly string[] NmXamlFiles =
    {
        "src/PeakCan.Host.App/Views/TraceViewerViewNmPanel.xaml",
        "src/PeakCan.Host.App/Views/NmInputWizardWindow.xaml",
    };

    private static readonly Regex StaticResourceRef = new(
        @"\{StaticResource\s+(?:ResourceKey\s*=\s*)?([A-Za-z0-9_.]+)\s*\}",
        RegexOptions.Compiled);

    private static readonly Regex XKeyDefinition = new(
        @"x:Key\s*=\s*""([^""]+)""",
        RegexOptions.Compiled);

    [Fact]
    public void NmPanel_HoverOverlayAndPopup_KeepCriticalProperties()
    {
        // 这些属性一旦被「顺手简化」，要么卡片常驻/空白，要么覆盖层抢走 seek 点击。
        var repoRoot = FindRepoRoot();
        repoRoot.Should().NotBeNull();
        var content = File.ReadAllText(
            Path.Combine(repoRoot!, "src/PeakCan.Host.App/Views/TraceViewerViewNmPanel.xaml"));

        content.Should().Contain("x:Name=\"NmSegmentPopup\"")
            .And.Contain("Placement=\"Top\"")
            .And.Contain("StaysOpen=\"True\"")
            .And.Contain("Canvas IsHitTestVisible=\"False\"",
                "准星覆盖层必须让鼠标事件穿透到活动条")
            .And.Contain("ElementName=NmTimelineTrackHost",
                "准星高度必须跟随时间条宿主")
            .And.Contain("MouseEnter=\"OnNmSegmentMouseEnter\"")
            .And.Contain("MouseLeave=\"OnNmSegmentMouseLeave\"")
            .And.Contain("ScrollChanged=\"OnNmTimelineScrollChanged\"",
                "滚动时必须收起不跟随目标的 Popup");
    }

    [Fact]
    public void NmAiJump_KeepsVisibleFeedbackPath()
    {
        // 用户报告过“点了按钮什么也没发生”：预填必须伴随切页 + 提示 + 聚焦，
        // 这三处任一被改掉都会退回“无反馈”状态。
        var repoRoot = FindRepoRoot();
        repoRoot.Should().NotBeNull();

        var nmPanel = File.ReadAllText(
            Path.Combine(repoRoot!, "src/PeakCan.Host.App/Views/TraceViewerViewNmPanel.xaml"));
        nmPanel.Should()
            .Contain("Command=\"{Binding SummarizeNmInAiCommand}\"")
            .And.Contain("AI 负责解释", "面板要说明 AI 与引擎的分工");
        // ShowOnDisabled 必须挂在“解读”按钮本身：禁用态还能看到“先选中一行”。
        Regex.IsMatch(nmPanel,
                "Content=\"在 AI 中解读选中事件\"[^>]*ToolTipService\\.ShowOnDisabled=\"True\"")
            .Should().BeTrue("禁用的解读按钮必须解释“先选中一行”");

        var chatPanel = File.ReadAllText(
            Path.Combine(repoRoot!, "src/PeakCan.Host.App/Views/TraceViewerViewChatPanel.xaml"));
        chatPanel.Should()
            .Contain("x:Name=\"ChatInputBox\"", "跳转后需要把焦点移到输入框")
            .And.Contain("Binding HasChatPrefillHint",
                "预填后必须有“按发送开始”的可见提示");

        // 焦点交接是“点按钮有反应”的一部分；仅 XAML 命名不足以证明会聚焦。
        var chatPanelCode = File.ReadAllText(
            Path.Combine(repoRoot!, "src/PeakCan.Host.App/Views/TraceViewerViewChatPanel.xaml.cs"));
        chatPanelCode.Should()
            .Contain("ChatInputFocusTicket", "必须监听 VM 的聚焦请求")
            .And.Contain("IsVisibleChanged",
                "Tab 切过来之前元素不可聚焦，必须等可见后再聚焦")
            .And.Contain("ChatInputBox.Focus()", "最终必须真的调用聚焦")
            .And.Contain("DispatcherPriority.Input",
                "焦点必须延后到布局完成之后");

        var viewer = File.ReadAllText(
            Path.Combine(repoRoot!, "src/PeakCan.Host.App/Views/TraceViewerView.xaml"));
        viewer.Should()
            .Contain("SelectedIndex=\"{Binding RightPanelSelectedIndex}\"",
                "VM 必须能把用户切到 AI 对话页")
            .And.Contain("Header=\"{Binding NmTabHeader}\"",
                "Tab 头要显示 warn 徽标");
    }

    [Fact]
    public void NmViews_ReferenceOnlyDefinedStaticResources()
    {
        var repoRoot = FindRepoRoot();
        repoRoot.Should().NotBeNull("test must be able to locate the repo root");

        var appKeys = XKeyDefinition
            .Matches(File.ReadAllText(Path.Combine(repoRoot!, "src/PeakCan.Host.App/App.xaml")))
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var relativePath in NmXamlFiles)
        {
            var fullPath = Path.Combine(repoRoot!, relativePath);
            var content = File.ReadAllText(fullPath);
            var localKeys = XKeyDefinition.Matches(content)
                .Select(m => m.Groups[1].Value)
                .ToHashSet(StringComparer.Ordinal);

            foreach (Match reference in StaticResourceRef.Matches(content))
            {
                var key = reference.Groups[1].Value;
                (localKeys.Contains(key) || appKeys.Contains(key))
                    .Should().BeTrue(
                        $"{relativePath} 引用了未定义的 StaticResource \"{key}\"：加载期会抛 XamlParseException");
            }
        }
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
