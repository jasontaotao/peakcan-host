using System.IO;
using FluentAssertions;
using Xunit;

namespace PeakCan.Host.App.Tests.Views;

public sealed class HilViewLayoutTests
{
    private static string ResolveHilViewPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PeakCan.Host.slnx")))
            dir = dir.Parent!;
        return Path.Combine(dir!.FullName, "src", "PeakCan.Host.App", "Views", "HilView.xaml");
    }

    [Fact]
    public void HilView_DoesNotContainLegacyEnglishButtonCopy()
    {
        var xaml = File.ReadAllText(ResolveHilViewPath());
        xaml.Should().NotContain("Content=\"Mode:\"");
        xaml.Should().NotContain("Content=\"Run\"");
        xaml.Should().NotContain("Content=\"Browse...\"");
        xaml.Should().NotContain("Content=\"Faults\"");
        xaml.Should().NotContain("Content=\"Analyze\"");
        xaml.Should().NotContain("Content=\"Open ECU Editor\"");
        xaml.Should().Contain("Content=\"停止\"");
        xaml.Should().NotContain("Header=\"Test Cases\"");
        xaml.Should().NotContain("Header=\"Results\"");
        xaml.Should().NotContain("Header=\"HTML Report\"");
    }

    [Fact]
    public void HilView_HasStopTrialDiagnosticsAndHistory()
    {
        var xaml = File.ReadAllText(ResolveHilViewPath());
        xaml.Should().Contain("Command=\"{Binding StopCommand}\"");
        xaml.Should().Contain("ItemsSource=\"{Binding TrialDiagnostics}\"");
        xaml.Should().Contain("Text=\"{Binding CaseLogDirectory}\"");
    }

    [Fact]
    public void HilView_HasHistoryRowActionsAndEmptyState()
    {
        var xaml = File.ReadAllText(ResolveHilViewPath());
        xaml.Should().Contain("OpenHistoryReportCommand");
        xaml.Should().Contain("LoadHistorySuiteCommand");
        xaml.Should().Contain("尚无运行记录");
        xaml.Should().Contain("{Binding CurrentCaseName");
        xaml.Should().Contain("{Binding RunElapsedText");
        xaml.Should().Contain("ContextMenuOpening=\"StepContextMenu_Opening\"");
    }

}