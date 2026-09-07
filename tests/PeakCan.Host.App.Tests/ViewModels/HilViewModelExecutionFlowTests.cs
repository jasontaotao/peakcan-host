using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.App.ViewModels;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.Host.Core.HIL.Analysis;
using PeakCan.Host.Infrastructure.HIL.Reporting;
using PeakCan.Host.Core.HIL;
using System.IO;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

public sealed class HilViewModelExecutionFlowTests
{
    [Fact]
    public void RerunFailed_IsDisabled_WhenOnlySkippedCasesRemain()
    {
        var vm = CreateViewModel();
        vm.SetLastResult(new TestSuiteResult("S", 2, 1, 0, 1, 100, [], [MakeCaseResult("case_1", true)]));

        Assert.False(vm.RerunFailedCommand.CanExecute(null));
    }

    [Fact]
    public void RerunFailed_IsEnabled_WhenFailedCasesRemain()
    {
        var vm = CreateViewModel();
        vm.SetLastResult(new TestSuiteResult("S", 1, 0, 1, 0, 100, [], [MakeCaseResult("case_1", false)]));

        Assert.True(vm.RerunFailedCommand.CanExecute(null));
    }

    [Fact]
    public async Task ProgressCallback_UpdatesCaseNamePercentAndCompletedCount()
    {
        var runner = Substitute.For<IHilRunnerService>();
        runner.RunAsync(Arg.Any<HilRunRequest>(), Arg.Do<IProgress<TestProgress>>(p =>
        {
            p.Report(new TestProgress(1, 2, "Case 1"));
        }), Arg.Any<CancellationToken>()).Returns(PassedResult());
        var vm = CreateViewModel(runner);
        vm.SuitePath = @"C:\suite.json";
        vm.DbcPath = @"C:\dbc.dbc";
        vm.SelectedMode = HilMode.TraceReplay;
        vm.TracePath = @"C:\trace.asc";

        await vm.RunCommand.ExecuteAsync(null);

        Assert.Equal("Case 1", vm.CurrentCaseName);
        Assert.Equal(1, vm.CompletedCases);
        Assert.Equal(50, vm.ProgressPercent);
    }

    [Fact]
    public async Task ExternalSuiteChange_ShowsBannerAndReloadPreservesCheckedIds()
    {
        var path = Path.Combine(Path.GetTempPath(), $"suite-{Guid.NewGuid():N}.suite.json");
        await File.WriteAllTextAsync(path, SuiteJson(["A", "B"]));
        var vm = CreateViewModel();
        vm.SuitePath = path;
        vm.ReloadSuiteCommand.Execute(null);
        vm.AvailableCases.First(c => c.Name == "B").IsSelected = false;
        File.WriteAllText(path, SuiteJson(["A", "B", "C"]));
        vm.CheckSuiteChange();

        Assert.True(vm.SuiteChangedExternally);
        vm.ReloadSuiteCommand.Execute(null);
        Assert.True(vm.AvailableCases.First(c => c.Name == "A").IsSelected);
        Assert.False(vm.AvailableCases.First(c => c.Name == "B").IsSelected);
    }

    private static HilViewModel CreateViewModel(IHilRunnerService? runner = null) => new(
        runner ?? Substitute.For<IHilRunnerService>(), NullLogger<HilViewModel>.Instance,
        Substitute.For<IFileDialogService>(), Substitute.For<IHilAnalysisService>(),
        Substitute.For<IHilReportService>());

    private static TestSuiteResult PassedResult() => new("S", 1, 1, 0, 0, 1, [], []);

    private static TestCaseResult MakeCaseResult(string name, bool passed) => new(
        name, name, passed, passed ? null : "failed", 1, 1, 0, 0, 0, 0, []);
    private static string SuiteJson(IEnumerable<string> names)
    {
        var cases = string.Join(",", names.Select(n => "{\"id\":\"" + n + "\",\"name\":\"" + n + "\"}"));
        return "{\"name\":\"S\",\"cases\":[" + cases + "],\"globalCaseFixtureKeys\":[],\"suiteFixtureKeys\":[],\"config\":{}}";
    }
}
