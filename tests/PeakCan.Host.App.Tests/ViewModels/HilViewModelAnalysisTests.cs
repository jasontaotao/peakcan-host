using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.App.ViewModels;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Analysis;
using PeakCan.Host.Infrastructure.HIL;
using PeakCan.Host.Infrastructure.HIL.Reporting;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// Sprint 16 Inc 2: HilViewModel AnalyzeAsync wiring — LLM failure analysis.
/// AnalyzeAsync runs only after a failed run, calls IHilAnalysisService, and
/// surfaces the result (or unavailability reason) into AnalysisResult.
/// </summary>
public sealed class HilViewModelAnalysisTests
{
    private static HilViewModel CreateViewModel(
        IHilRunnerService? runner = null,
        IHilAnalysisService? analysis = null,
        IHilReportService? reportService = null)
    {
        var r = runner ?? Substitute.For<IHilRunnerService>();
        var log = NullLogger<HilViewModel>.Instance;
        var fd = Substitute.For<IFileDialogService>();
        var a = analysis ?? Substitute.For<IHilAnalysisService>();
        return new HilViewModel(r, log, fd, a,
            reportService ?? Substitute.For<IHilReportService>());
    }

    private static TestSuiteResult AllPassedResult() => new(
        "test", TotalCases: 1, PassedCases: 1, FailedCases: 0, SkippedCases: 0,
        ElapsedMs: 100, SetupFailures: Array.Empty<string>(),
        CaseResults: new[] { new TestCaseResult("tc1", "Case1", true, null, 100, 1, 1, 0, 0, 0, Array.Empty<StepResult>()) });

    private static TestSuiteResult FailedResult() => new(
        "test", TotalCases: 1, PassedCases: 0, FailedCases: 1, SkippedCases: 0,
        ElapsedMs: 100, SetupFailures: Array.Empty<string>(),
        CaseResults: new[] { new TestCaseResult("tc1", "Case1", false, "signal mismatch", 100, 1, 0, 1, 0, 0, Array.Empty<StepResult>()) });

    /// <summary>Run the VM once with the given result, returning the VM wired to that runner.</summary>
    private static async Task<HilViewModel> RunOnceAsync(TestSuiteResult result, IHilAnalysisService? analysis = null, bool enableAnalyze = false)
    {
        var runner = Substitute.For<IHilRunnerService>();
        runner.RunAsync(Arg.Any<HilRunRequest>(), Arg.Any<IProgress<TestProgress>>(), Arg.Any<CancellationToken>())
            .Returns(result);
        var vm = CreateViewModel(runner: runner, analysis: analysis);
        vm.DbcPath = "x.dbc";
        vm.SuitePath = "y.json";
        vm.TracePath = "t.asc";
        vm.EnableAnalyze = enableAnalyze;
        await vm.RunCommand.ExecuteAsync(null);
        return vm;
    }

    [Fact]
    public async Task AnalyzeAsync_LastResultNull_ReturnsEarly()
    {
        var analysis = Substitute.For<IHilAnalysisService>();
        var vm = CreateViewModel(analysis: analysis);
        // No prior run -> _lastResult is null.

        await vm.AnalyzeCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, vm.AnalysisResult);
        await analysis.DidNotReceive().AnalyzeAsync(Arg.Any<TestSuiteResult>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnalyzeAsync_AllPassed_ReturnsEarly()
    {
        var analysis = Substitute.For<IHilAnalysisService>();
        var vm = await RunOnceAsync(AllPassedResult(), analysis);

        await vm.AnalyzeCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, vm.AnalysisResult);
        await analysis.DidNotReceive().AnalyzeAsync(Arg.Any<TestSuiteResult>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnalyzeAsync_ServiceReturnsContent_UpdatesAnalysisResult()
    {
        var analysis = Substitute.For<IHilAnalysisService>();
        analysis.AnalyzeAsync(Arg.Any<TestSuiteResult>(), Arg.Any<CancellationToken>())
            .Returns(AnalysisResult.Success("Root cause: undervoltage"));
        var vm = await RunOnceAsync(FailedResult(), analysis);

        await vm.AnalyzeCommand.ExecuteAsync(null);

        Assert.Equal("Root cause: undervoltage", vm.AnalysisResult);
        await analysis.Received(1).AnalyzeAsync(Arg.Any<TestSuiteResult>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnalyzeAsync_ServiceUnavailable_ShowsReason()
    {
        var analysis = Substitute.For<IHilAnalysisService>();
        analysis.AnalyzeAsync(Arg.Any<TestSuiteResult>(), Arg.Any<CancellationToken>())
            .Returns(AnalysisResult.Unavailable("API key not configured"));
        var vm = await RunOnceAsync(FailedResult(), analysis);

        await vm.AnalyzeCommand.ExecuteAsync(null);

        Assert.Contains("API key not configured", vm.AnalysisResult);
    }

    [Fact]
    public async Task AnalyzeAsync_ServiceThrowsOperationCancelled_ShowsTimeout()
    {
        // Code-review M2: HttpClient.Timeout surfaces as TaskCanceledException
        // which must be surfaced to the user, not silently swallowed.
        var analysis = Substitute.For<IHilAnalysisService>();
        analysis.AnalyzeAsync(Arg.Any<TestSuiteResult>(), Arg.Any<CancellationToken>())
            .Returns<Task<AnalysisResult?>>(Task.FromException<AnalysisResult?>(new OperationCanceledException()));
        var vm = await RunOnceAsync(FailedResult(), analysis);

        await vm.AnalyzeCommand.ExecuteAsync(null);

        Assert.Equal("Analysis timed out.", vm.AnalysisResult);
    }

    [Fact]
    public async Task CanAnalyze_NotRunningWithFailedResult_ReturnsTrue()
    {
        var vm = await RunOnceAsync(FailedResult());

        Assert.False(vm.IsRunning);
        Assert.True(vm.AnalyzeCommand.CanExecute(null));
    }

    [Fact]
    public async Task CanAnalyze_IsRunning_ReturnsFalse()
    {
        // Simulate an in-flight run: the runner never completes until we signal.
        var runner = Substitute.For<IHilRunnerService>();
        var tcs = new TaskCompletionSource<TestSuiteResult>();
        runner.RunAsync(Arg.Any<HilRunRequest>(), Arg.Any<IProgress<TestProgress>>(), Arg.Any<CancellationToken>())
            .Returns(tcs.Task);
        var vm = CreateViewModel(runner: runner);
        vm.DbcPath = "x.dbc";
        vm.SuitePath = "y.json";
        vm.TracePath = "t.asc";

        var runTask = vm.RunCommand.ExecuteAsync(null);
        // Give the command a beat to flip IsRunning.
        await Task.Delay(50);

        Assert.True(vm.IsRunning);
        Assert.False(vm.AnalyzeCommand.CanExecute(null));

        tcs.SetResult(FailedResult());
        await runTask;
    }

    // --- Phase 7 Unit A: EnableAnalyze auto-analyze ---

    private static TestSuiteResult EmptySuiteResult() => new(
        "empty", TotalCases: 0, PassedCases: 0, FailedCases: 0, SkippedCases: 0,
        ElapsedMs: 0, SetupFailures: Array.Empty<string>(), CaseResults: Array.Empty<TestCaseResult>());

    [Fact]
    public async Task RunAsync_EnableAnalyzeTrueWithFailures_AutoAnalyzes()
    {
        var analysis = Substitute.For<IHilAnalysisService>();
        analysis.AnalyzeAsync(Arg.Any<TestSuiteResult>(), Arg.Any<CancellationToken>())
            .Returns(AnalysisResult.Success("Auto analysis result"));
        var vm = await RunOnceAsync(FailedResult(), analysis, enableAnalyze: true);

        await analysis.Received(1).AnalyzeAsync(Arg.Any<TestSuiteResult>(), Arg.Any<CancellationToken>());
        Assert.Equal("Auto analysis result", vm.AnalysisResult);
    }

    [Fact]
    public async Task RunAsync_EnableAnalyzeFalseWithFailures_DoesNotAutoAnalyze()
    {
        var analysis = Substitute.For<IHilAnalysisService>();
        _ = await RunOnceAsync(FailedResult(), analysis, enableAnalyze: false);

        await analysis.DidNotReceive().AnalyzeAsync(Arg.Any<TestSuiteResult>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_EnableAnalyzeTrueAllPassed_DoesNotAutoAnalyze()
    {
        var analysis = Substitute.For<IHilAnalysisService>();
        _ = await RunOnceAsync(AllPassedResult(), analysis, enableAnalyze: true);

        await analysis.DidNotReceive().AnalyzeAsync(Arg.Any<TestSuiteResult>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_EnableAnalyzeTrueEmptySuite_DoesNotAutoAnalyze()
    {
        var analysis = Substitute.For<IHilAnalysisService>();
        _ = await RunOnceAsync(EmptySuiteResult(), analysis, enableAnalyze: true);

        await analysis.DidNotReceive().AnalyzeAsync(Arg.Any<TestSuiteResult>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_EnableAnalyzeTrueAnalysisUnavailable_ShowsReason()
    {
        var analysis = Substitute.For<IHilAnalysisService>();
        analysis.AnalyzeAsync(Arg.Any<TestSuiteResult>(), Arg.Any<CancellationToken>())
            .Returns(AnalysisResult.Unavailable("API key not configured"));
        var vm = await RunOnceAsync(FailedResult(), analysis, enableAnalyze: true);

        Assert.Contains("API key not configured", vm.AnalysisResult);
    }
}
