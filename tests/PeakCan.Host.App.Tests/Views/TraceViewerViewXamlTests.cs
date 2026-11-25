using System.IO;
using FluentAssertions;
using PeakCan.Host.App.Tests.Collections;

namespace PeakCan.Host.App.Tests.Views;

/// <summary>
/// v3.11.6 PATCH regression guard: the master-radio XAML antipattern
/// (nested Binding in ConverterParameter) caused a XamlParseException
/// after the first .asc load. This test asserts that no XAML file in the
/// production tree references the deleted <c>MasterRadioConverter</c>
/// or the deleted <c>MasterRadio</c> resource key, so the antipattern
/// cannot be re-introduced silently in a future PATCH.
///
/// STA-bound (XAML inspection is fine on MTA, but the test class joins
/// <see cref="WpfAppTestCollection"/> for consistency with other
/// TraceViewer-related test classes).
/// </summary>
[Collection(WpfAppTestCollection.Name)]
public class TraceViewerViewXamlTests
{
    private static readonly string[] TrackedFiles =
    {
        "src/PeakCan.Host.App/Views/TraceViewerView.xaml",
        "src/PeakCan.Host.App/Windows/UdsWindow.xaml",        // v3.11.3 sibling
        "src/PeakCan.Host.App/Windows/MultiFrameSendWindow.xaml",
        "src/PeakCan.Host.App/Composition/Converters/MasterRadioConverter.cs",
        "src/PeakCan.Host.App/App.xaml",
    };

    [Fact]
    public void NoProductionFile_References_MasterRadioConverter_Or_ResourceKey()
    {
        // Locate repo root: walk up from this test file until we find
        // the .git directory. This avoids hard-coding an absolute path
        // (CI uses a different repo root than local dev machines).
        var repoRoot = FindRepoRoot();
        repoRoot.Should().NotBeNull("test must be able to locate the repo root");

        foreach (var relPath in TrackedFiles)
        {
            var full = Path.Combine(repoRoot!, relPath);
            if (!File.Exists(full))
            {
                // UdsWindow.xaml is v3.11.3 sibling; skip if not on disk yet.
                continue;
            }
            var content = File.ReadAllText(full);
            content.Should().NotContain("MasterRadio",
                $"{relPath} must not reference MasterRadio (deleted in v3.11.6 PATCH; nested Binding in ConverterParameter antipattern)");
            content.Should().NotContain("MasterRadioConverter",
                $"{relPath} must not reference MasterRadioConverter (deleted in v3.11.6 PATCH)");
        }
    }

    [Fact]
    public void TraceViewer_UsesFluentIcons_InsteadOfTextGlyphs()
    {
        var repoRoot = FindRepoRoot();
        repoRoot.Should().NotBeNull();

        var traceViewer = File.ReadAllText(Path.Combine(repoRoot!, "src/PeakCan.Host.App/Views/TraceViewerView.xaml"));
        var chatPanel = File.ReadAllText(Path.Combine(repoRoot!, "src/PeakCan.Host.App/Views/TraceViewerViewChatPanel.xaml"));

        traceViewer.Should().NotContain("Content=\"● 当前\"");
        traceViewer.Should().NotContain("Content=\"● 比较\"");
        traceViewer.Should().NotContain("Content=\"[▼ Collapse]\"");
        traceViewer.Should().Contain("icons:FluentIconGlyphs.Record");
        traceViewer.Should().Contain("icons:FluentIconGlyphs.ChevronDown");

        chatPanel.Should().NotContain("工具 ▼");
        chatPanel.Should().Contain("icons:FluentIconGlyphs.ChevronDown");
    }
    /// <summary>
    /// J1939 重组面板必须限定在「监视列表」页内。放在 TabControl 之外（固定底行）
    /// 会让 AI 对话、网络管理等无关 tab 永久损失 180px 纵向空间。
    /// </summary>
    [Fact]
    public void J1939ReassemblyPanel_IsScopedToWatchListTab()
    {
        var repoRoot = FindRepoRoot();
        repoRoot.Should().NotBeNull();

        var doc = System.Xml.Linq.XDocument.Load(
            Path.Combine(repoRoot!, "src/PeakCan.Host.App/Views/TraceViewerView.xaml"));

        var panel = doc.Descendants()
            .Single(e => e.Name.LocalName == "GroupBox"
                && (string?)e.Attribute("Header") == "J1939 重组");

        var hostTab = panel.Ancestors().FirstOrDefault(a => a.Name.LocalName == "TabItem");
        hostTab.Should().NotBeNull("重组面板必须位于某个 TabItem 内");
        ((string?)hostTab!.Attribute("Header")).Should().Be("监视列表");
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
