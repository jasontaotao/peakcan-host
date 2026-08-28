using System.Collections.ObjectModel;
using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.App.ViewModels;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Analysis;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.HIL;
using PeakCan.Host.Infrastructure.HIL.Reporting;
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
        IFileDialogService? fileDialog = null,
        IHilReportService? reportService = null)
    {
        var r = runner ?? Substitute.For<IHilRunnerService>();
        var log = NullLogger<HilViewModel>.Instance;
        var fd = fileDialog ?? Substitute.For<IFileDialogService>();
        return new HilViewModel(r, log, fd, Substitute.For<IHilAnalysisService>(),
            reportService ?? Substitute.For<IHilReportService>());
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

    // --- ECU script path ---

    [Fact]
    public void EcuScriptPath_Empty_RunButtonDisabled()
    {
        var vm = CreateViewModel();
        vm.DbcPath = "x.dbc";
        vm.SuitePath = "y.json";
        vm.SelectedMode = HilMode.VirtualEcu;
        vm.EcuScriptPath = ""; // no script path

        Assert.False(vm.RunCommand.CanExecute(null));
    }

    // --- Case-log capture (Task 10: P1/P11) ---

    [Fact]
    public void CaptureCaseLogs_DefaultsTrue()
    {
        var vm = CreateViewModel();
        Assert.True(vm.CaptureCaseLogs);
    }

    [Fact]
    public async Task RunAsync_PassesCaptureCaseLogs_WhenChecked()
    {
        var runner = Substitute.For<IHilRunnerService>();
        HilRunRequest? lastRequest = null;
        runner.RunAsync(Arg.Do<HilRunRequest>(r => lastRequest = r), Arg.Any<IProgress<TestProgress>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AllPassedResult()));

        var vm = CreateViewModel(runner);
        vm.DbcPath = "x.dbc";
        vm.SuitePath = "y.json";
        vm.TracePath = "x.asc";
        vm.SelectedMode = HilMode.TraceReplay;
        vm.CaptureCaseLogs = true;

        await vm.RunCommand.ExecuteAsync(null);

        Assert.NotNull(lastRequest);
        Assert.True(lastRequest!.CaptureCaseLogs);
    }

    [Fact]
    public async Task RunAsync_StatusMessage_AppendsCaseLogDir()
    {
        var runner = Substitute.For<IHilRunnerService>();
        runner.RunAsync(Arg.Any<HilRunRequest>(), Arg.Any<IProgress<TestProgress>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AllPassedResult()));
        runner.LastCaseLogDirectory.Returns(@"C:\logs\case-logs");

        var vm = CreateViewModel(runner);
        vm.DbcPath = "x.dbc";
        vm.SuitePath = "y.json";
        vm.TracePath = "x.asc";
        vm.SelectedMode = HilMode.TraceReplay;
        vm.CaptureCaseLogs = true;

        await vm.RunCommand.ExecuteAsync(null);

        Assert.Contains("case logs", vm.StatusMessage);
        Assert.Contains(@"C:\logs\case-logs", vm.StatusMessage);
    }

    // ── Spec v3 §3.4: HIL 执行按顺序绑定已连接通道 ─────────────────

    private const string MultiChannelSuiteJson = """
    {
      "name": "MultiChannel",
      "channels": [
        { "name": "bus-a", "handle": "", "baudRate": null, "fd": false, "dbcPath": null, "udsRequestId": null, "udsResponseId": null },
        { "name": "bus-b", "handle": "", "baudRate": null, "fd": false, "dbcPath": null, "udsRequestId": null, "udsResponseId": null }
      ],
      "cases": [ { "id": "c1", "name": "TP", "steps": [ { "parameters": { "$kind": "delay", "Milliseconds": 10 } } ] } ]
    }
    """;

    // G2: suite 声明 per-channel DBC/UDS ID（UDS ID 为 JSON 数字——hil-core uint 形态，0x7E0=2016 等）
    private const string MultiChannelSuiteWithPerChannelParamsJson = """
    {
      "name": "MultiChannel",
      "channels": [
        { "name": "bus-a", "handle": "", "baudRate": null, "fd": false, "dbcPath": "A.dbc", "udsRequestId": 2016, "udsResponseId": 2024 },
        { "name": "bus-b", "handle": "", "baudRate": null, "fd": false, "dbcPath": "B.dbc", "udsRequestId": 1760, "udsResponseId": 1768 }
      ],
      "cases": [ { "id": "c1", "name": "TP", "steps": [ { "parameters": { "$kind": "delay", "Milliseconds": 10 } } ] } ]
    }
    """;

    [Fact]
    public async Task RunAsync_WithSuitePerChannelParams_PropagatesToHardwareChannels()
    {
        // G2 host 读侧：suite 带 dbcPath/udsRequestId/udsResponseId → HardwareChannels 透传（不再丢弃）
        var suitePath = Path.GetTempFileName();
        File.WriteAllText(suitePath, MultiChannelSuiteWithPerChannelParamsJson);
        HilRunRequest? captured = null;
        var runner = Substitute.For<IHilRunnerService>();
        runner.RunAsync(Arg.Do<HilRunRequest>(r => captured = r),
                Arg.Any<IProgress<TestProgress>>(), Arg.Any<CancellationToken>())
            .Returns(AllPassedResult());
        var vm = new HilViewModel(
            runner, NullLogger<HilViewModel>.Instance, Substitute.For<IFileDialogService>(),
            Substitute.For<IHilAnalysisService>(), Substitute.For<IHilReportService>(),
            connectedChannels: () =>
            [
                new HilViewModel.ConnectedChannel(0x51, BaudRate.Can500kbps, Fd: false),
                new HilViewModel.ConnectedChannel(0x52, BaudRate.Can125kbps, Fd: false),
            ]);
        vm.SuitePath = suitePath;
        vm.SelectedMode = HilMode.Hardware;
        vm.HardwareChannel = "USB1";
        try
        {
            await vm.RunCommand.ExecuteAsync(null);

            captured.Should().NotBeNull();
            captured!.HardwareChannels.Should().HaveCount(2);
            // per-channel 三字段透传（当前 BuildHardwareChannels 透传 null → 断言失败 = RED）
            captured.HardwareChannels![0].DbcPath.Should().Be("A.dbc");
            captured.HardwareChannels[0].UdsRequestId.Should().Be(2016);
            captured.HardwareChannels[0].UdsResponseId.Should().Be(2024);
            captured.HardwareChannels[1].DbcPath.Should().Be("B.dbc");
            captured.HardwareChannels[1].UdsRequestId.Should().Be(1760);
            captured.HardwareChannels[1].UdsResponseId.Should().Be(1768);
            // 连接参数仍取已连通道实际值（spec v3 T13 语义不变）
            captured.HardwareChannels[0].BaudRate.Should().Be(BaudRate.Can500kbps);
        }
        finally
        {
            File.Delete(suitePath);
        }
    }

    // ── G3: USBx 下拉绑已连接通道（spec §4）──────────────────

    private static HilViewModel NewVm(Func<IReadOnlyList<HilViewModel.ConnectedChannel>>? connected = null)
        => new(
            Substitute.For<IHilRunnerService>(), NullLogger<HilViewModel>.Instance,
            Substitute.For<IFileDialogService>(), Substitute.For<IHilAnalysisService>(),
            Substitute.For<IHilReportService>(), connectedChannels: connected);

    [Fact]
    public void RefreshAvailableChannels_WithConnectedChannels_PopulatesDropdownAndDefaultsFirst()
    {
        var vm = NewVm(() =>
        [
            new HilViewModel.ConnectedChannel(0x51, BaudRate.Can500kbps, Fd: false),
            new HilViewModel.ConnectedChannel(0x52, BaudRate.Can125kbps, Fd: false),
        ]);

        vm.RefreshAvailableChannels();

        vm.AvailableChannels.Should().HaveCount(2);
        vm.AvailableChannels[0].Handle.Should().Be("USB1");      // n = handle - 0x50
        vm.AvailableChannels[0].Display.Should().Contain("USB1");
        vm.AvailableChannels[0].Display.Should().Contain("500 kbps");
        vm.AvailableChannels[1].Handle.Should().Be("USB2");
        vm.HardwareChannel.Should().Be("USB1");                  // 默认第一个已连接
    }

    [Fact]
    public void RefreshAvailableChannels_EmptyProvider_EmptiesDropdownAndClearsChannel()
    {
        var vm = NewVm(() => []);
        vm.HardwareChannel = "USB3";

        vm.RefreshAvailableChannels();

        vm.AvailableChannels.Should().BeEmpty();
        vm.HardwareChannel.Should().Be("", "无已连接通道 → 清空 HardwareChannel, CanRun false");
    }

    [Fact]
    public void RefreshAvailableChannels_PreservesLastSelection()
    {
        var vm = NewVm(() =>
        [
            new HilViewModel.ConnectedChannel(0x51, BaudRate.Can500kbps, Fd: false),
            new HilViewModel.ConnectedChannel(0x52, BaudRate.Can125kbps, Fd: false),
        ]);
        vm.RefreshAvailableChannels();
        vm.HardwareChannel.Should().Be("USB1");

        // 用户切到 USB2 → 再刷新 → 保留 USB2（不跳回第一个，防连接目标漂移）
        vm.HardwareChannel = "USB2";
        vm.RefreshAvailableChannels();

        vm.HardwareChannel.Should().Be("USB2");
    }

    [Fact]
    public void RefreshAvailableChannels_MultiChannelSuite_FlagsIsMultiChannel()
    {
        // G3: suite 声明多通道（declaredCount>1）→ Hardware 下拉置灰（IsMultiChannelSuite）
        var suitePath = Path.GetTempFileName();
        File.WriteAllText(suitePath, MultiChannelSuiteJson);   // bus-a/bus-b 两路
        var vm = NewVm(() =>
        [
            new HilViewModel.ConnectedChannel(0x51, BaudRate.Can500kbps, Fd: false),
            new HilViewModel.ConnectedChannel(0x52, BaudRate.Can125kbps, Fd: false),
        ]);
        vm.SuitePath = suitePath;
        try
        {
            vm.RefreshAvailableChannels();
            vm.IsMultiChannelSuite.Should().BeTrue();
        }
        finally
        {
            File.Delete(suitePath);
        }
    }

    [Fact]
    public void RefreshAvailableChannels_SingleChannelSuite_NotMultiChannel()
    {
        // 单通道 suite（无 channels 或 1 路）→ 下拉可配（IsMultiChannelSuite false）
        var vm = NewVm(() =>
        [
            new HilViewModel.ConnectedChannel(0x51, BaudRate.Can500kbps, Fd: false),
        ]);
        vm.RefreshAvailableChannels();
        vm.IsMultiChannelSuite.Should().BeFalse();
    }

    // ── G4: 文件后缀区分（spec §5）──────────────────

    [Fact]
    public void BrowseSuite_UsesSuiteJsonFilter()
    {
        var fd = Substitute.For<IFileDialogService>();
        fd.ShowOpenDialog(Arg.Any<string>()).Returns((string?)null);
        var vm = CreateViewModel(fileDialog: fd);

        vm.BrowseSuiteCommand.Execute(null);

        fd.Received().ShowOpenDialog(Arg.Is<string>(f =>
            f.Contains("*.suite.json", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void BrowseEcu_UsesEcuJsonFilter()
    {
        var fd = Substitute.For<IFileDialogService>();
        fd.ShowOpenDialog(Arg.Any<string>()).Returns((string?)null);
        var vm = CreateViewModel(fileDialog: fd);

        vm.BrowseEcuCommand.Execute(null);

        fd.Received().ShowOpenDialog(Arg.Is<string>(f =>
            f.Contains("*.ecu.json", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void BrowseMatrix_UsesMatrixJsonFilter()
    {
        var fd = Substitute.For<IFileDialogService>();
        fd.ShowOpenDialog(Arg.Any<string>()).Returns((string?)null);
        var vm = CreateViewModel(fileDialog: fd);

        vm.BrowseMatrixCommand.Execute(null);

        fd.Received().ShowOpenDialog(Arg.Is<string>(f =>
            f.Contains("*.matrix.json", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void LoadCaseList_NotSuiteFile_SetsStatusMessage()
    {
        // G4 内容硬校验：打开无 cases 字段的 JSON → 明确提示（当前静默 catch → RED）
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """{ "name": "not-a-suite" }""");
        var fd = Substitute.For<IFileDialogService>();
        fd.ShowOpenDialog(Arg.Any<string>()).Returns(path);
        var vm = CreateViewModel(fileDialog: fd);
        try
        {
            vm.BrowseSuiteCommand.Execute(null);
            vm.StatusMessage.Should().Contain("不是测试套件文件");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RunAsync_WithSuiteChannels_AndConnectedChannels_BindsByOrder()
    {
        // suite 声明 bus-a/bus-b；host 已连接 2 路（0x51@500k, 0x52@125k）
        // → HardwareChannels[i] = suite.Channels[i].Name + 已连通道 i 的波特率/FD
        var suitePath = Path.GetTempFileName();
        File.WriteAllText(suitePath, MultiChannelSuiteJson);
        HilRunRequest? captured = null;
        var runner = Substitute.For<IHilRunnerService>();
        runner.RunAsync(Arg.Do<HilRunRequest>(r => captured = r),
                Arg.Any<IProgress<TestProgress>>(), Arg.Any<CancellationToken>())
            .Returns(AllPassedResult());
        var vm = new HilViewModel(
            runner, NullLogger<HilViewModel>.Instance, Substitute.For<IFileDialogService>(),
            Substitute.For<IHilAnalysisService>(), Substitute.For<IHilReportService>(),
            connectedChannels: () =>
            [
                new HilViewModel.ConnectedChannel(0x51, BaudRate.Can500kbps, Fd: false),
                new HilViewModel.ConnectedChannel(0x52, BaudRate.Can125kbps, Fd: false),
            ]);
        vm.SuitePath = suitePath;
        vm.SelectedMode = HilMode.Hardware;
        vm.HardwareChannel = "USB1";
        try
        {
            await vm.RunCommand.ExecuteAsync(null);

            captured.Should().NotBeNull("RunAsync 应被调且捕获 request");
            captured!.HardwareChannels.Should().HaveCount(2);
            captured.HardwareChannels![0].Name.Should().Be("bus-a");
            captured.HardwareChannels[0].BaudRate.Should().Be(BaudRate.Can500kbps);
            captured.HardwareChannels[0].Fd.Should().BeFalse();
            captured.HardwareChannels[1].Name.Should().Be("bus-b");
            captured.HardwareChannels[1].BaudRate.Should().Be(BaudRate.Can125kbps);
        }
        finally
        {
            File.Delete(suitePath);
        }
    }

    [Fact]
    public async Task RunAsync_SuiteChannelsMoreThanConnected_TruncatesAndWarns()
    {
        // suite 声明 2 路，host 只连 1 路 → HardwareChannels 截断到 1 + 状态栏提示
        var suitePath = Path.GetTempFileName();
        File.WriteAllText(suitePath, MultiChannelSuiteJson);
        HilRunRequest? captured = null;
        var runner = Substitute.For<IHilRunnerService>();
        runner.RunAsync(Arg.Do<HilRunRequest>(r => captured = r),
                Arg.Any<IProgress<TestProgress>>(), Arg.Any<CancellationToken>())
            .Returns(AllPassedResult());
        var vm = new HilViewModel(
            runner, NullLogger<HilViewModel>.Instance, Substitute.For<IFileDialogService>(),
            Substitute.For<IHilAnalysisService>(), Substitute.For<IHilReportService>(),
            connectedChannels: () =>
            [
                new HilViewModel.ConnectedChannel(0x51, BaudRate.Can500kbps, Fd: true),
            ]);
        vm.SuitePath = suitePath;
        vm.SelectedMode = HilMode.Hardware;
        vm.HardwareChannel = "USB1";
        try
        {
            await vm.RunCommand.ExecuteAsync(null);

            captured!.HardwareChannels.Should().HaveCount(1, "按少的截断");
            vm.StatusMessage.Should().Contain("仅");
        }
        finally
        {
            File.Delete(suitePath);
        }
    }

    [Fact]
    public async Task RunAsync_NoSuiteChannels_NullHardwareChannels()
    {
        // 零回归：suite 无 channels 字段 → HardwareChannels null（单通道路径）
        var suitePath = Path.GetTempFileName();
        File.WriteAllText(suitePath, """{"name":"S","cases":[{"id":"c1","name":"T","steps":[{"parameters":{"$kind":"delay","Milliseconds":10}}]}]}""");
        HilRunRequest? captured = null;
        var runner = Substitute.For<IHilRunnerService>();
        runner.RunAsync(Arg.Do<HilRunRequest>(r => captured = r),
                Arg.Any<IProgress<TestProgress>>(), Arg.Any<CancellationToken>())
            .Returns(AllPassedResult());
        var vm = new HilViewModel(
            runner, NullLogger<HilViewModel>.Instance, Substitute.For<IFileDialogService>(),
            Substitute.For<IHilAnalysisService>(), Substitute.For<IHilReportService>(),
            connectedChannels: () => [new HilViewModel.ConnectedChannel(0x51, BaudRate.Can500kbps, Fd: false)]);
        vm.SuitePath = suitePath;
        vm.SelectedMode = HilMode.Hardware;
        vm.HardwareChannel = "USB1";
        try
        {
            await vm.RunCommand.ExecuteAsync(null);
            captured!.HardwareChannels.Should().BeNull("suite 无 channels → 单通道零回归");
        }
        finally { File.Delete(suitePath); }
    }

    [Fact]
    public async Task RunAsync_ProviderNull_NullHardwareChannels()
    {
        // 零回归：connectedChannels provider null（默认构造）→ HardwareChannels null
        var suitePath = Path.GetTempFileName();
        File.WriteAllText(suitePath, """{"name":"S","channels":[{"name":"bus-a"}],"cases":[{"id":"c1","name":"T","steps":[{"parameters":{"$kind":"delay","Milliseconds":10}}]}]}""");
        HilRunRequest? captured = null;
        var runner = Substitute.For<IHilRunnerService>();
        runner.RunAsync(Arg.Do<HilRunRequest>(r => captured = r),
                Arg.Any<IProgress<TestProgress>>(), Arg.Any<CancellationToken>())
            .Returns(AllPassedResult());
        var vm = CreateViewModel(runner); // 默认 ctor 无 provider
        vm.SuitePath = suitePath;
        vm.SelectedMode = HilMode.Hardware;
        vm.HardwareChannel = "USB1";
        try
        {
            await vm.RunCommand.ExecuteAsync(null);
            captured!.HardwareChannels.Should().BeNull("无 provider → null");
        }
        finally { File.Delete(suitePath); }
    }

    // ── 报告侧多通道 DBC 字典接线（Task 11 闭环）────────────

    [Fact]
    public async Task RunAsync_WithPerChannelDbcs_ReportUsesMultiChannelOverload()
    {
        // runner 返回 per-channel DBC 字典 → 报告走多通道重载（fallbackDbc = LastDbcDocument）
        var runner = Substitute.For<IHilRunnerService>();
        runner.RunAsync(Arg.Any<HilRunRequest>(), Arg.Any<IProgress<TestProgress>>(), Arg.Any<CancellationToken>())
            .Returns(AllPassedResult());
        var dbcs = new Dictionary<ChannelId, DbcDocument> { [new ChannelId(0x51)] = Util.MakeDbc("A") };
        runner.LastPerChannelDbcs.Returns(dbcs);
        runner.LastDbcDocument.Returns(Util.MakeDbc("Global"));

        var reportService = Substitute.For<IHilReportService>();
        reportService.Generate(Arg.Any<TestSuiteResult>(), (IReadOnlyDictionary<ChannelId, DbcDocument>?)null, Arg.Any<DbcDocument?>())
            .Returns(new HilReportResult("", @"C:\report.html"));
        var vm = new HilViewModel(
            runner, NullLogger<HilViewModel>.Instance, Substitute.For<IFileDialogService>(),
            Substitute.For<IHilAnalysisService>(), reportService);
        vm.DbcPath = "x.dbc";
        vm.SuitePath = "y.json";
        vm.TracePath = "x.asc";
        vm.SelectedMode = HilMode.TraceReplay;

        await vm.RunCommand.ExecuteAsync(null);

        reportService.Received(1).Generate(
            Arg.Any<TestSuiteResult>(),
            Arg.Is<IReadOnlyDictionary<ChannelId, DbcDocument>>(d => d!.ContainsKey(new ChannelId(0x51))),
            Arg.Is<DbcDocument?>(f => f != null && f.SourcePath == "Global"));
        reportService.DidNotReceive().Generate(Arg.Any<TestSuiteResult>(), Arg.Any<DbcDocument?>());
    }

    [Fact]
    public async Task RunAsync_NoPerChannelDbcs_ReportUsesSingleDbcOverload()
    {
        // running 无 per-channel DBC（单通道）→ 回落单 DBC 重载（零回归）
        var runner = Substitute.For<IHilRunnerService>();
        runner.RunAsync(Arg.Any<HilRunRequest>(), Arg.Any<IProgress<TestProgress>>(), Arg.Any<CancellationToken>())
            .Returns(AllPassedResult());
        runner.LastPerChannelDbcs.Returns((IReadOnlyDictionary<ChannelId, DbcDocument>?)null);

        var reportService = Substitute.For<IHilReportService>();
        reportService.Generate(Arg.Any<TestSuiteResult>(), Arg.Any<DbcDocument?>())
            .Returns(new HilReportResult("", @"C:\report.html"));
        var vm = new HilViewModel(
            runner, NullLogger<HilViewModel>.Instance, Substitute.For<IFileDialogService>(),
            Substitute.For<IHilAnalysisService>(), reportService);
        vm.DbcPath = "x.dbc";
        vm.SuitePath = "y.json";
        vm.TracePath = "x.asc";
        vm.SelectedMode = HilMode.TraceReplay;

        await vm.RunCommand.ExecuteAsync(null);

        reportService.Received(1).Generate(Arg.Any<TestSuiteResult>(), Arg.Any<DbcDocument?>());
        reportService.DidNotReceive().Generate(Arg.Any<TestSuiteResult>(), Arg.Any<IReadOnlyDictionary<ChannelId, DbcDocument>>(), Arg.Any<DbcDocument?>());
    }

    private static class Util
    {
        public static DbcDocument MakeDbc(string src)
            => new("", new List<Node>(), new List<Message>(),
                new Dictionary<uint, Message>(), new Dictionary<string, ValueTable>(), SourcePath: src);
    }
}
