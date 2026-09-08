using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core.HIL.Analysis;
using PeakCan.Host.Infrastructure.HIL.Reporting;
using PeakCan.Host.Core.HIL;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.HIL;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

public sealed class HilViewModelStopTests
{
    [Fact]
    public async Task Stop_CancelsRun_RendersPartialResultAndReport()
    {
        var reportService = Substitute.For<IHilReportService>();
        reportService.Generate(Arg.Any<TestSuiteResult>(), Arg.Any<DbcDocument?>())
            .Returns(new HilReportResult("<html></html>", @"C:\reports\partial.html"));
        var runner = new CancellingRunner();
        var vm = new HilViewModel(runner, NullLogger<HilViewModel>.Instance, Substitute.For<IFileDialogService>(),
            Substitute.For<IHilAnalysisService>(), reportService);
        vm.SuitePath = @"C:\suite.json";
        vm.DbcPath = @"C:\dbc.dbc";
        vm.SelectedMode = HilMode.TraceReplay;
        vm.TracePath = @"C:\trace.asc";

        Assert.False(vm.StopCommand.CanExecute(null));

        var runTask = vm.RunCommand.ExecuteAsync(null);
        while (!vm.IsRunning) await Task.Delay(10);
        Assert.True(vm.StopCommand.CanExecute(null));
        vm.StopCommand.Execute(null);
        await runTask;

        Assert.False(vm.IsRunning);
        Assert.False(vm.StopCommand.CanExecute(null));
        Assert.True(runner.LastToken.CanBeCanceled);
        Assert.Single(vm.Results);
        Assert.Contains("已取消（完成 1/2）", vm.StatusMessage);
        Assert.Equal(@"C:\reports\partial.html", vm.LatestReportPath);
    }

    private sealed class CancellingRunner : IHilRunnerService
    {
        public CancellationToken LastToken { get; private set; }
        public DbcDocument? LastDbcDocument => null;
        public IReadOnlyDictionary<ChannelId, DbcDocument>? LastPerChannelDbcs => null;
        public string? LastCaseLogDirectory => null;

        public async Task<TestSuiteResult> RunAsync(HilRunRequest request, IProgress<TestProgress>? progress = null, CancellationToken ct = default)
        {
            LastToken = ct;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(250);
            try { await Task.Delay(1000, cts.Token); } catch (OperationCanceledException) { }
            return PartialResult();
        }

        private static TestSuiteResult PartialResult()
        {
            var caseResult = new TestCaseResult("case_1", "Case 1", true, null, 10, 1, 1, 0, 0, 0, Array.Empty<StepResult>());
            return new TestSuiteResult("S", 2, 1, 0, 1, 10, Array.Empty<string>(), new[] { caseResult });
        }
    }
}
