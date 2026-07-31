using System.Collections.ObjectModel;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core;
using PeakCan.Host.Core.HIL;
using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.HIL;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// Tests for HilViewModel (Sprint 12: WPF HIL Panel).
/// Covers Mode→path mapping, browse commands, progress reporting,
/// result tree structure, and ECU editor save/validation.
/// </summary>
public sealed class HilViewModelTests
{
    private static HilViewModel CreateViewModel(
        IHilRunnerService? runner = null,
        IFileDialogService? fileDialog = null)
    {
        var r = runner ?? Substitute.For<IHilRunnerService>();
        var log = NullLogger<HilViewModel>.Instance;
        var fd = fileDialog ?? Substitute.For<IFileDialogService>();
        return new HilViewModel(r, log, fd);
    }

    private static TestSuiteResult AllPassedResult() => new(
        "test", TotalCases: 1, PassedCases: 1, FailedCases: 0, SkippedCases: 0,
        ElapsedMs: 100, SetupFailures: Array.Empty<string>(),
        CaseResults: new[]
        {
            new TestCaseResult("tc1", "Case1", true, null, 100, 1, 1, 0, 0, 0,
                new[]
                {
                    new StepResult(0, TestCaseStepKind.SendFrame, "Send", StepStatus.Passed, "ok", null, null, 50),
                }),
        });

    private static TestSuiteResult FailedResult() => new(
        "test", TotalCases: 1, PassedCases: 0, FailedCases: 1, SkippedCases: 0,
        ElapsedMs: 100, SetupFailures: Array.Empty<string>(),
        CaseResults: new[]
        {
            new TestCaseResult("tc1", "Case1", false, "signal mismatch", 100, 1, 0, 1, 0, 0,
                new[]
                {
                    new StepResult(0, TestCaseStepKind.AssertSignal, "Assert", StepStatus.Failed,
                        "expected 1 got 0", "0", "1", 50, new[]
                        {
                            new CanFrame(new CanId(0x123, FrameFormat.Standard),
                                new ReadOnlyMemory<byte>(new byte[] { 0x01, 0x02, 0x03 }),
                                FrameFlags.None, ChannelId.None, new Timestamp(0)),
                        }),
                }),
        });

    // --- Mode → path mapping ---

    [Fact]
    public void ModeSwitch_ToVirtualEcu_SetsEcuScriptPathActive()
    {
        var cli = new HilRunRequest("x.dbc", "y.json", EcuScriptPath: "ecu.json", Mode: HilMode.VirtualEcu)
            .ToCliArgs();

        Assert.Equal("ecu.json", cli.EcuScriptPath);
        Assert.Null(cli.TracePath);
        Assert.Null(cli.HardwareChannel);
        Assert.Null(cli.MatrixPath);
    }

    [Fact]
    public void ModeSwitch_ToHardware_SetsHardwareChannelActive()
    {
        var cli = new HilRunRequest("x.dbc", "y.json", HardwareChannel: "USB1", Mode: HilMode.Hardware)
            .ToCliArgs();

        Assert.Equal("USB1", cli.HardwareChannel);
        Assert.Null(cli.TracePath);
        Assert.Null(cli.EcuScriptPath);
    }

    [Fact]
    public void HilRunRequest_ModeField_ToCliArgs_MapsCorrectly()
    {
        // TraceReplay
        var r1 = new HilRunRequest("d", "s", TracePath: "t.asc", Mode: HilMode.TraceReplay).ToCliArgs();
        Assert.Equal("t.asc", r1.TracePath);
        Assert.Null(r1.HardwareChannel);

        // Matrix
        var r2 = new HilRunRequest("d", "s", MatrixPath: "m.json", Mode: HilMode.Matrix).ToCliArgs();
        Assert.Equal("m.json", r2.MatrixPath);
        Assert.Null(r2.EcuScriptPath);

        // VirtualEcu
        var r3 = new HilRunRequest("d", "s", EcuScriptPath: "e.json", Mode: HilMode.VirtualEcu).ToCliArgs();
        Assert.Equal("e.json", r3.EcuScriptPath);
    }

    // --- Browse commands ---

    [Fact]
    public void BrowseCommand_DbcFilter_CallsFileDialogService()
    {
        var fd = Substitute.For<IFileDialogService>();
        fd.ShowOpenDialog(Arg.Is<string>(s => s.Contains("dbc", StringComparison.OrdinalIgnoreCase)))
            .Returns("C:\\test.dbc");

        var vm = CreateViewModel(fileDialog: fd);
        vm.BrowseDbcCommand.Execute(null);

        Assert.Equal("C:\\test.dbc", vm.DbcPath);
        fd.Received().ShowOpenDialog(Arg.Any<string>());
    }

    [Fact]
    public void BrowseCommand_UserCancels_NoChange()
    {
        var fd = Substitute.For<IFileDialogService>();
        fd.ShowOpenDialog(Arg.Any<string>()).Returns((string?)null);

        var vm = CreateViewModel(fileDialog: fd);
        vm.DbcPath = "existing.dbc";
        vm.BrowseDbcCommand.Execute(null);

        Assert.Equal("existing.dbc", vm.DbcPath); // unchanged
    }

    // --- Progress ---

    [Fact]
    public async Task Progress_UpdatesPercentComplete()
    {
        var runner = Substitute.For<IHilRunnerService>();
        IProgress<TestProgress>? capturedProgress = null;
        runner.RunAsync(Arg.Any<HilRunRequest>(), Arg.Do<IProgress<TestProgress>>(p => capturedProgress = p), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AllPassedResult()));

        var vm = CreateViewModel(runner);
        vm.DbcPath = "x.dbc";
        vm.SuitePath = "y.json";
        vm.TracePath = "x.asc";
        vm.SelectedMode = HilMode.TraceReplay;

        await vm.RunCommand.ExecuteAsync(null);

        Assert.NotNull(capturedProgress);
        capturedProgress!.Report(new TestProgress(5, 10));

        // Progress<T>.Report posts to the thread pool when no SynchronizationContext
        // is captured — yield so the callback executes before asserting.
        await Task.Delay(50);

        Assert.True(vm.ProgressPercent > 0);
    }

    // --- Result tree structure ---

    [Fact]
    public async Task ResultTree_FailedCase_HasStepAndFrameNodes()
    {
        var runner = Substitute.For<IHilRunnerService>();
        runner.RunAsync(Arg.Any<HilRunRequest>(), Arg.Any<IProgress<TestProgress>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(FailedResult()));

        var vm = CreateViewModel(runner);
        vm.DbcPath = "x.dbc";
        vm.SuitePath = "y.json";
        vm.TracePath = "x.asc";
        vm.SelectedMode = HilMode.TraceReplay;

        await vm.RunCommand.ExecuteAsync(null);

        Assert.Single(vm.ResultsTree);
        var caseNode = Assert.IsType<TestCaseNode>(vm.ResultsTree[0]);
        Assert.Single(caseNode.Steps);
        var step = caseNode.Steps[0];
        Assert.Equal("Failed", step.Status);
        Assert.Single(step.Frames); // failed step captured a frame
        Assert.Equal("0x123", step.Frames[0].CanId);
    }

    [Fact]
    public async Task ResultTree_AllPassed_NoFrameNodes()
    {
        var runner = Substitute.For<IHilRunnerService>();
        runner.RunAsync(Arg.Any<HilRunRequest>(), Arg.Any<IProgress<TestProgress>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AllPassedResult()));

        var vm = CreateViewModel(runner);
        vm.DbcPath = "x.dbc";
        vm.SuitePath = "y.json";
        vm.TracePath = "x.asc";
        vm.SelectedMode = HilMode.TraceReplay;

        await vm.RunCommand.ExecuteAsync(null);

        Assert.Single(vm.ResultsTree);
        var caseNode = Assert.IsType<TestCaseNode>(vm.ResultsTree[0]);
        Assert.Single(caseNode.Steps);
        Assert.Empty(caseNode.Steps[0].Frames); // no frames captured when passed
    }

    // --- ECU editor ---

    [Fact]
    public void EcuEditor_SaveAndRun_WritesTempFile_SetsEcuScriptPath()
    {
        var vm = CreateViewModel();
        vm.EcuEditorJson = """{"name":"Test","canIds":{"requestId":"0x7E0","responseId":"0x7E8"},"rules":[]}""";

        Assert.True(vm.SaveEcuCommand.CanExecute(null));
        vm.SaveEcuCommand.Execute(null);

        Assert.False(string.IsNullOrEmpty(vm.EcuScriptPath));
        Assert.True(File.Exists(vm.EcuScriptPath));
        var content = File.ReadAllText(vm.EcuScriptPath);
        Assert.Contains("Test", content);
    }

    [Fact]
    public void EcuEditor_EmptyJson_RunButtonDisabled()
    {
        var vm = CreateViewModel();
        vm.DbcPath = "x.dbc";
        vm.SuitePath = "y.json";
        vm.SelectedMode = HilMode.VirtualEcu;
        vm.EcuEditorJson = ""; // empty
        vm.EcuScriptPath = ""; // no script saved

        Assert.False(vm.RunCommand.CanExecute(null));
    }
}
