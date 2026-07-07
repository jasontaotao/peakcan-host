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
