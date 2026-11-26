using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace PeakCan.Host.App.Tests.Views;

/// <summary>
/// 回归（用户报告：AI 聊天发送抛 XamlParseException）：
/// <c>Run.Text</c> 的绑定默认模式是 TwoWay（不是直觉上的 OneWay），
/// 绑定到只读属性（如 <c>ObservableCollection.Count</c>、<c>{ get; }</c> 属性）
/// 会在渲染时抛 “无法对只读属性进行 TwoWay 绑定”。
/// 项目里其它 Run 绑定都显式写了 Mode=OneWay，此守卫防止再漏。
/// </summary>
public class RunTextBindingGuardTests
{
    // StringFormat 里可能嵌套花括号（如 {0}），所以这里按引号边界匹配整个绑定表达式。
    private static readonly Regex RunTextBinding = new(
        @"<Run\b[^>]*\bText\s*=\s*""\{Binding[^""]*\}""",
        RegexOptions.Compiled);

    // OneWayToSource 同样会写只读源，不能靠子串 Contains 放过；
    // OneWay 后的 \b 已排除 OneWayToSource，负向断言再兜一层。
    private static readonly Regex ExplicitOneWay = new(
        @"\bMode\s*=\s*OneWay\b(?!\s*ToSource)",
        RegexOptions.Compiled);

    [Fact]
    public void All_Run_Text_Bindings_Are_Explicitly_OneWay()
    {
        var root = FindRepoRoot();
        root.Should().NotBeNull();

        var appDir = Path.Combine(root!, "src", "PeakCan.Host.App");
        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(appDir, "*.xaml", SearchOption.AllDirectories))
        {
            if (file.Contains(
                $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (Match match in RunTextBinding.Matches(File.ReadAllText(file)))
            {
                if (ExplicitOneWay.IsMatch(match.Value))
                    continue;
                violations.Add(
                    $"{Path.GetRelativePath(root!, file)}: {match.Value}");
            }
        }

        violations.Should().BeEmpty(
            "Run.Text 默认 TwoWay；绑只读属性必须显式 Mode=OneWay，否则渲染时抛 XamlParseException");
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            // git worktree 里 .git 是文件而不是目录，两种形式都要认。
            var git = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
