using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.Uds;
using PeakCan.Host.App.ViewModels.Uds;
using PeakCan.Host.Core.Analysis.Uds;
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Core.Uds.Database;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Uds.Database;
using PeakCan.HIL.Core.Uds.Odx;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels.Uds;

public sealed class UdsLogAnalysisViewModelTests
{
    private static ReplayFrame Frame(uint id, params byte[] data)
        => new(Timestamp: 0, Id: id, Dlc: (byte)data.Length, Data: data,
               Flags: FrameFlags.None, IsExtended: false, Channel: 1, IsTransmit: null);

    private static ReplayFrame[] ThreeFrameLog() => new[]
    {
        Frame(0x7E0, 0x02, 0x10, 0x01),
        Frame(0x7E8, 0x06, 0x50, 0x01, 0x00, 0x32, 0x01, 0xF4),
        Frame(0x123, 0xAA),
    };

    private static UdsLogAnalysisViewModel Vm(
        IUdsOfflineTraceLoader loader, ITraceViewerChatHandoffService? handoff = null,
        IUdsExportPrompt? exportPrompt = null, OdxImportViewModel? odxImport = null)
        => new(loader, new UdsOfflineAnalyzer(), handoff ?? new FakeHandoff(), new FakeDialogs(),
            exportPrompt ?? new FakeExportPrompt(UdsExportChoice.Cancel),
            new DidDatabase(userJsonPath: null), new RoutineDatabase(userJsonPath: null),
            odxImport ?? new OdxImportViewModel(
                new FakeOdxService(OdxImportResult.Ok(0, 0, 0, Array.Empty<string>()))));

    [Fact]
    public async Task Analyze_produces_report_and_visible_rows()
    {
        var vm = Vm(new FakeLoader(ThreeFrameLog()));
        vm.FilePath = "fake.asc";
        await vm.AnalyzeAsync();

        vm.Report.Should().NotBeNull();
        vm.VisibleTransactions.Should().ContainSingle();
        vm.Status.Should().Contain("分析完成");
    }

    [Fact]
    public async Task No_matching_frames_shows_address_specific_empty_state()
    {
        var vm = Vm(new FakeLoader(new[] { Frame(0x123, 0x01) }));
        vm.FilePath = "fake.asc";
        await vm.AnalyzeAsync();

        vm.Status.Should().Contain("当前设置下未识别到诊断事务");
        vm.Report.Should().NotBeNull();
        vm.Report!.Transactions.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancel_produces_partial_report()
    {
        var vm = Vm(new FakeLoader(ThreeFrameLog()));
        vm.FilePath = "fake.asc";
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await vm.AnalyzeAsync(cts.Token);

        vm.IsPartial.Should().BeTrue();
        vm.Status.Should().Contain("部分");
    }

    [Fact]
    public async Task Interpret_selected_sends_handoff_without_provider_call()
    {
        var handoff = new FakeHandoff();
        var vm = Vm(new FakeLoader(ThreeFrameLog()), handoff);
        vm.FilePath = "fake.asc";
        await vm.AnalyzeAsync();
        vm.SelectedTransaction = vm.Report!.Transactions[0];

        vm.InterpretSelectedCommand.Execute(null);

        handoff.Payload.Should().NotBeNull();
        handoff.Payload!.Prompt.Should().Contain(vm.Report.ReportId.ToString());
    }

    [Fact]
    public async Task Export_cancel_writes_nothing()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "r.md");
        var vm = Vm(new FakeLoader(ThreeFrameLog()),
            exportPrompt: new FakeExportPrompt(UdsExportChoice.Cancel, savePath: path));
        vm.FilePath = "fake.asc";
        await vm.AnalyzeAsync();

        await vm.ExportAsync();

        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public async Task Export_raw_writes_markdown_and_reports_path()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "r.md");
        var vm = Vm(new FakeLoader(ThreeFrameLog()),
            exportPrompt: new FakeExportPrompt(UdsExportChoice.Raw, savePath: path));
        vm.FilePath = "fake.asc";
        await vm.AnalyzeAsync();

        await vm.ExportAsync();

        File.Exists(path).Should().BeTrue();
        File.ReadAllText(path).Should().Contain("# UDS 离线分析报告");
        vm.Status.Should().Contain("已导出");
    }

    [Fact]
    public async Task Failed_load_clears_the_previous_report()
    {
        var loader = new FakeLoader(ThreeFrameLog());
        var vm = Vm(loader);
        vm.FilePath = "fake.asc";
        await vm.AnalyzeAsync();
        vm.Report.Should().NotBeNull();

        loader.FailNext = true;
        await vm.AnalyzeAsync();

        vm.Report.Should().BeNull("旧报告不能在新分析失败后继续冒充当前报告");
        vm.Status.Should().Contain("读取失败");
    }

    [Fact]
    public async Task Stale_run_cannot_overwrite_the_newer_report()
    {
        var loader = new StagedLoader();
        var vm = Vm(loader);
        vm.FilePath = "slow.asc";

        var slowRun = vm.AnalyzeAsync();
        vm.FilePath = "fast.asc";
        await vm.AnalyzeAsync();
        vm.Status.Should().Contain("分析完成");

        loader.ReleaseSlow();
        await slowRun;

        vm.Status.Should().Contain("分析完成", "旧任务被取消后不得把自己的状态写回去");
        vm.Report!.SourcePath.Should().Be("fast.asc");
    }

    [Fact]
    public async Task Filter_with_no_match_shows_a_distinct_hint()
    {
        var vm = Vm(new FakeLoader(ThreeFrameLog()));
        vm.FilePath = "fake.asc";
        await vm.AnalyzeAsync();

        vm.FilterText = "no-such-service";

        vm.VisibleTransactions.Should().BeEmpty();
        vm.FilterEmptyHint.Should().NotBeNullOrWhiteSpace();
        vm.Status.Should().Contain("分析完成", "筛选无结果不改写分析状态");
    }

    [Fact]
    public async Task Odx_import_updates_status_and_asks_for_reanalysis()
    {
        var odx = new OdxImportViewModel(
            new FakeOdxService(OdxImportResult.Ok(1, 0, 0, Array.Empty<string>())));
        var vm = Vm(new FakeLoader(ThreeFrameLog()), odxImport: odx);

        await odx.ImportAsync("x.odx");

        vm.OdxStatusSummary.Should().Contain("ODX");
        vm.OdxReloadHint.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Closing_the_window_cancels_and_discards_the_report()
    {
        var vm = Vm(new FakeLoader(ThreeFrameLog()));
        vm.FilePath = "fake.asc";
        await vm.AnalyzeAsync();
        vm.Report.Should().NotBeNull();

        vm.StopForWindowClose();

        vm.Report.Should().BeNull("重开窗口必须是新的空状态");
        vm.VisibleTransactions.Should().BeEmpty();
        vm.Status.Should().Be("未选择文件");
        vm.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task Status_distinguishes_reading_the_file_from_analyzing()
    {
        var loader = new StagedLoader();
        var vm = Vm(loader);
        vm.FilePath = "slow.asc";

        var run = vm.AnalyzeAsync();
        await loader.Started;
        vm.Status.Should().Contain("读取文件");

        loader.ReleaseSlow();
        await run;
    }

    [Fact]
    public async Task Unclosed_transaction_shows_an_explicit_conclusion()
    {
        // 只有请求、没有响应：必须说明“未闭合”，而不是留一个 None。
        var vm = Vm(new FakeLoader(new[] { Frame(0x7E0, 0x02, 0x10, 0x03) }));
        vm.FilePath = "fake.asc";
        await vm.AnalyzeAsync();
        vm.SelectedTransaction = vm.Report!.Transactions.Single();

        vm.SelectedConclusion.Should().Contain("未闭合");
    }

    private sealed class StagedLoader : IUdsOfflineTraceLoader
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _first = true;

        public Task Started => _started.Task;

        public void ReleaseSlow() => _gate.TrySetResult();

        public async Task<UdsOfflineTraceLoadResult> LoadAsync(string path, CancellationToken ct = default)
        {
            if (!_first) return new UdsOfflineTraceLoadResult(ThreeFrameLog(), "ASC");
            _first = false;
            _started.TrySetResult();
            await _gate.Task;
            return new UdsOfflineTraceLoadResult(ThreeFrameLog(), "ASC");
        }
    }

    private sealed class FakeLoader(ReplayFrame[] frames) : IUdsOfflineTraceLoader
    {
        public bool FailNext { get; set; }

        public Task<UdsOfflineTraceLoadResult> LoadAsync(string path, CancellationToken ct = default)
            => FailNext
                ? Task.FromException<UdsOfflineTraceLoadResult>(new UdsOfflineLoadException("文件不存在：fake.asc"))
                : Task.FromResult(new UdsOfflineTraceLoadResult(frames, "ASC"));
    }

    private sealed class FakeHandoff : ITraceViewerChatHandoffService
    {
        public UdsChatHandoffPayload? Payload { get; private set; }
        public void Handoff(UdsChatHandoffPayload payload) => Payload = payload;
    }

    private sealed class FakeDialogs : IFileDialogService
    {
        public string? ShowOpenDialog(string filter) => null;
        public string? ShowSaveDialog(string filter, string? defaultExt, string? initialDirectory) => null;
    }

    private sealed class FakeExportPrompt(UdsExportChoice choice, string? savePath = null) : IUdsExportPrompt
    {
        public string? ChooseSavePath(out bool isCsv)
        {
            isCsv = false;
            return savePath;
        }

        public bool ConfirmOverwrite(string path) => true;
        public UdsExportChoice AskRedaction() => choice;
    }

    private sealed class FakeOdxService(OdxImportResult result) : IOdxImportService
    {
        public Task<OdxImportResult> ImportAsync(string odxPath, CancellationToken ct = default)
            => Task.FromResult(result);
    }
}
