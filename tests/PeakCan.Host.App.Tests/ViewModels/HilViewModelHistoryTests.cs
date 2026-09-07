using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.App.Services.HilHistory;
using PeakCan.Host.App.ViewModels;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.Host.Core.HIL.Analysis;
using PeakCan.Host.Infrastructure.HIL.Reporting;
using PeakCan.Host.Core.HIL;
using System.IO;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

public sealed class HilViewModelHistoryTests
{
    [Fact]
    public async Task NormalCompletion_WritesHistory()
    {
        var store = new HilRunHistoryStore(NullLogger<HilRunHistoryStore>.Instance, Path.GetTempFileName());
        var runner = Substitute.For<IHilRunnerService>();
        var vm = CreateVm(store, runner);
        await RunAsync(vm, runner, runner => runner.RunAsync(Arg.Any<HilRunRequest>(), Arg.Any<IProgress<TestProgress>>(), Arg.Any<CancellationToken>()).Returns(PassResult()));

        Assert.Single(vm.RunHistory);
        Assert.True(vm.RunHistory[0].Passed);
        Assert.False(vm.RunHistory[0].Cancelled);
    }

    [Fact]
    public async Task UserCancellation_WritesCancelledHistory()
    {
        var store = new HilRunHistoryStore(NullLogger<HilRunHistoryStore>.Instance, Path.GetTempFileName());
        var runner = Substitute.For<IHilRunnerService>();
        var vm = CreateVm(store, runner);
        runner.RunAsync(Arg.Any<HilRunRequest>(), Arg.Any<IProgress<TestProgress>>(), Arg.Any<CancellationToken>())
            .Returns<Task<TestSuiteResult>>(async call =>
            {
                var ct = call.Arg<CancellationToken>();
                while (!ct.IsCancellationRequested) await Task.Delay(5);
                return PartialResult(1, 2, cancelledCase: false);
            });
        var runTask = vm.RunCommand.ExecuteAsync(null);
        while (!vm.IsRunning) await Task.Delay(5);
        vm.StopCommand.Execute(null);
        await runTask;

        Assert.Single(vm.RunHistory);
        Assert.True(vm.RunHistory[0].Cancelled);
    }

    [Fact]
    public async Task Timeout_WritesPartialNotCancelledHistory()
    {
        var store = new HilRunHistoryStore(NullLogger<HilRunHistoryStore>.Instance, Path.GetTempFileName());
        var runner = Substitute.For<IHilRunnerService>();
        var vm = CreateVm(store, runner);
        await RunAsync(vm, runner, runner => runner.RunAsync(Arg.Any<HilRunRequest>(), Arg.Any<IProgress<TestProgress>>(), Arg.Any<CancellationToken>()).Returns(PartialResult(1, 2, timeoutCase: true)));

        Assert.Single(vm.RunHistory);
        Assert.False(vm.RunHistory[0].Cancelled);
        Assert.Equal("套件超时", vm.RunHistory[0].ErrorMessage);
    }

    [Fact]
    public async Task Exception_WritesErrorHistoryWithoutResult()
    {
        var store = new HilRunHistoryStore(NullLogger<HilRunHistoryStore>.Instance, Path.GetTempFileName());
        var runner = Substitute.For<IHilRunnerService>();
        var vm = CreateVm(store, runner);
        await RunAsync(vm, runner, runner => runner.RunAsync(Arg.Any<HilRunRequest>(), Arg.Any<IProgress<TestProgress>>(), Arg.Any<CancellationToken>())
            .Returns<TestSuiteResult>(x => throw new InvalidOperationException("boom")));

        Assert.Single(vm.RunHistory);
        Assert.False(vm.RunHistory[0].Passed);
        Assert.Contains("boom", vm.RunHistory[0].ErrorMessage);
    }

    [Fact]
    public async Task Exception_WritesParsedCaseCounts()
    {
        var store = new HilRunHistoryStore(NullLogger<HilRunHistoryStore>.Instance, Path.GetTempFileName());
        var runner = Substitute.For<IHilRunnerService>();
        var vm = CreateVm(store, runner);
        var suitePath = Path.Combine(Path.GetTempPath(), $"history-{Guid.NewGuid():N}.suite.json");
        string[] caseNames = { "A", "B", "C" };
        File.WriteAllText(suitePath, SuiteJson(caseNames));
        vm.SuitePath = suitePath;
        vm.DbcPath = @"C:\dbc.dbc";
        vm.SelectedMode = HilMode.TraceReplay;
        vm.TracePath = @"C:\trace.asc";
        vm.ReloadSuiteCommand.Execute(null);
        runner.RunAsync(Arg.Any<HilRunRequest>(), Arg.Any<IProgress<TestProgress>>(), Arg.Any<CancellationToken>())
            .Returns<TestSuiteResult>(x => throw new InvalidOperationException("boom"));
        await vm.RunAsync();

        var record = Assert.Single(vm.RunHistory);
        Assert.Equal(3, record.TotalCases);
        Assert.Equal(0, record.PassedCases);
        Assert.Equal(3, record.FailedCases);
    }

    private static HilViewModel CreateVm(HilRunHistoryStore? history, IHilRunnerService runner) => new(
        runner, NullLogger<HilViewModel>.Instance,
        Substitute.For<IFileDialogService>(), Substitute.For<IHilAnalysisService>(),
        Substitute.For<IHilReportService>(), runHistoryStore: history);

    private static async Task RunAsync(HilViewModel vm, IHilRunnerService runner, Action<IHilRunnerService> configure)
    {
        vm.SuitePath = @"C:\suite.json";
        vm.DbcPath = @"C:\dbc.dbc";
        vm.SelectedMode = HilMode.TraceReplay;
        vm.TracePath = @"C:\trace.asc";
        configure(runner);
        await vm.RunAsync();
    }

    private static TestSuiteResult PassResult() => new("S", 1, 1, 0, 0, 1, [], []);

    private static TestSuiteResult PartialResult(int completed, int total, bool cancelledCase = false, bool timeoutCase = false)
    {
        var reason = cancelledCase ? "已取消" : timeoutCase ? "套件超时" : null;
        var caseResult = new TestCaseResult("case_1", "Case 1", false, reason, 1, 0, 0, 1, 0, 0, []);
        return new TestSuiteResult("S", total, 0, 1, total - completed, 1, [], [caseResult]);
    }
    internal static string SuiteJson(IEnumerable<string> names)
    {
        var cases = string.Join(",", names.Select(n => "{\"id\":\"" + n + "\",\"name\":\"" + n + "\"}"));
        return "{\"name\":\"S\",\"cases\":[" + cases + "],\"globalCaseFixtureKeys\":[],\"suiteFixtureKeys\":[],\"config\":{}}";
    }
}
