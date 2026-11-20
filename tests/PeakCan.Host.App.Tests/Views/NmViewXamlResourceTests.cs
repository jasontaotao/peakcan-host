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
