using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core;
using PeakCan.Host.Core.HIL;
using PeakCan.Host.Core.HIL.Analysis;
using PeakCan.Host.Infrastructure.HIL.Reporting;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// Phase 7 Unit C: HilViewModel 报告逻辑 — RunAsync 后 LatestReportPath 填充、
/// 生成失败降级（不阻断测试结果）、OpenReport no-op、5 参 ctor。
/// </summary>
public sealed class HilViewModelReportTests
{
    private static HilViewModel CreateViewModel(
        IHilRunnerService? runner = null,
        IHilReportService? reportService = null)
    {
        var r = runner ?? Substitute.For<IHilRunnerService>();
        var log = NullLogger<HilViewModel>.Instance;
        var fd = Substitute.For<IFileDialogService>();
        return new HilViewModel(r, log, fd, Substitute.For<IHilAnalysisService>(),
            reportService ?? Substitute.For<IHilReportService>());
    }

    private static TestSuiteResult AllPassedResult() => new(
        "test", TotalCases: 1, PassedCases: 1, FailedCases: 0, SkippedCases: 0,
        ElapsedMs: 100, SetupFailures: Array.Empty<string>(),
        CaseResults: Array.Empty<TestCaseResult>());

    private static TestSuiteResult FailedResult() => new(
        "test", TotalCases: 1, PassedCases: 0, FailedCases: 1, SkippedCases: 0,
        ElapsedMs: 100, SetupFailures: Array.Empty<string>(),
        CaseResults: new[]
        {
            new TestCaseResult("tc1", "Case1", false, "signal mismatch", 100, 1, 0, 1, 0, 0, Array.Empty<StepResult>()),
        });

    private static IHilRunnerService MockRunner(TestSuiteResult result)
    {
        var runner = Substitute.For<IHilRunnerService>();
        runner.RunAsync(Arg.Any<HilRunRequest>(), Arg.Any<IProgress<TestProgress>>(), Arg.Any<CancellationToken>())
            .Returns(result);
        return runner;
    }

    private static void ConfigureRun(HilViewModel vm)
    {
        vm.SuitePath = @"C:\suite.json";
        vm.DbcPath = @"C:\db.dbc";
        vm.SelectedMode = HilMode.TraceReplay;
        vm.TracePath = @"C:\trace.asc";
    }

    [Fact]
    public async Task RunAsync_Success_FillsReportPath()
    {
        var reportService = Substitute.For<IHilReportService>();
        reportService.Generate(Arg.Any<TestSuiteResult>())
            .Returns(new HilReportResult("<html>ok</html>", @"C:\reports\hil-report-1.html"));
        var vm = CreateViewModel(MockRunner(AllPassedResult()), reportService);
        ConfigureRun(vm);

        await vm.RunCommand.ExecuteAsync(null);

        Assert.Equal(@"C:\reports\hil-report-1.html", vm.LatestReportPath);
        Assert.False(vm.ShowReportError);
        Assert.Equal("", vm.ReportError);
    }

    [Fact]
    public async Task RunAsync_ReportServiceThrows_DegradesGracefully()
    {
        var reportService = Substitute.For<IHilReportService>();
        reportService.Generate(Arg.Any<TestSuiteResult>())
            .Returns(x => throw new InvalidOperationException("disk full"));
        var vm = CreateViewModel(MockRunner(FailedResult()), reportService);
        ConfigureRun(vm);

        await vm.RunCommand.ExecuteAsync(null);

        // 报告失败降级：错误状态设置，但测试结果仍填充（Results 非空）、不抛。
        Assert.True(vm.ShowReportError);
        Assert.Contains("disk full", vm.ReportError);
        Assert.Single(vm.Results);
        Assert.Equal("", vm.LatestReportPath);
    }

    [Fact]
    public void OpenReport_NoPath_NoOp()
    {
        var vm = CreateViewModel();
        // 未运行过：LatestReportPath 为空，OpenReport 不抛。
        vm.OpenReportCommand.Execute(null);
        Assert.Equal("", vm.LatestReportPath);
    }

    [Fact]
    public void ctor_AcceptsFiveParams()
    {
        var vm = CreateViewModel();
        Assert.NotNull(vm);
    }

    [Fact]
    public void OnReportWebView2InitFailed_SetsErrorState()
    {
        var vm = CreateViewModel();
        vm.OnReportWebView2InitFailed(
            new InvalidOperationException("runtime missing"),
            "WebView2 runtime 未安装或损坏: runtime missing. 请安装 WebView2 Evergreen Runtime.");

        // LOW-1: WebView2 init 失败 → VM 错误状态设置（日志 + fallback 提示）。
        Assert.True(vm.ShowReportError);
        Assert.Contains("WebView2 runtime", vm.ReportError);
    }
}
