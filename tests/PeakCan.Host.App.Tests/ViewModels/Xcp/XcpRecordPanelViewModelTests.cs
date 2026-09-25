using A2lEditor.Core;
using A2lEditor.Core.Layout;
using A2lEditor.Core.Model;
using FluentAssertions;
using PeakCan.Host.App.ViewModels.Xcp;
using PeakCan.Host.Core.Xcp.Record;
using PeakCan.Host.Core.Xcp.Receive;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels.Xcp;

/// <summary>
/// S4-T5 记录面板 VM 测试（spec D5）：Start 门禁、通道=关注集快照、
/// 采集 Stop 先停记录、故障红字隔离、空关注集门禁。
/// </summary>
public sealed class XcpRecordPanelViewModelTests
{
    // 每次调用独立目录：同秒 Start 的文件名会冲突（xcp_yyyyMMdd_HHmmss.mf4）。
    private static string NewTempDir() =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"s5vm_{Guid.NewGuid():N}");

    private static A2lDocument Doc()
    {
        var measurements = new[]
        {
            new A2lMeasurement("EngineSpeed", "rpm fixture", A2lDataType.UBYTE, "CM_RPM",
                "0", "0", "0", "100", 0x2000, new LineRange(1, 1), Format: "%6.2"),
        };
        var compuMethods = new[]
        {
            new A2lCompuMethod("CM_RPM", "speed", "IDENTICAL", "%.2f", "rpm",
                new IdenticalConversion(), new LineRange(10, 10)),
        };
        var module = new A2lModule("M", "m", measurements,
            Array.Empty<A2lCharacteristic>(), Array.Empty<A2lAxisPts>(), compuMethods,
            Array.Empty<A2lRecordLayout>(), Array.Empty<A2lGroup>(), null,
            Array.Empty<A2lAxisDescr>(), Array.Empty<A2lUserRights>(),
            Array.Empty<A2lVersionInfo>(), Array.Empty<A2lAxisPtsX>(),
            new LineRange(1, 1));
        return new A2lDocument(A2lVersion.V1_6x, "P", "", "",
            new A2lModCommon("", A2lByteOrder.MSB_LAST, null, null, null, new LineRange(1, 1)),
            new[] { module }, "", 1);
    }

    private static ValueContract Contract(string name)
    {
        var contracts = new ContractSet(Doc());
        contracts.TryGet(name, out var contract).Should().BeTrue();
        return contract!;
    }

    private static XcpCardPanelViewModel CardsWithWatch()
    {
        var cards = new XcpCardPanelViewModel();
        cards.AddWatch("EngineSpeed", "MEASUREMENT", Contract("EngineSpeed"));
        return cards;
    }

    private static XcpMdfRecordSink Sink(string? dir = null) =>
        new(new XcpMdfRecordSinkOptions { Directory = dir ?? NewTempDir() });

    private static XcpDaqSample Sample(double value) => new(
        new(2, 1, 0, "EngineSpeed", 0, 2, 0, 0x2000, null), value, DateTimeOffset.UtcNow);

    [Fact]
    public void Start_record_is_blocked_when_acquisition_not_running()
    {
        var sink = Sink(NewTempDir());
        var vm = new XcpRecordPanelViewModel(
            cards: CardsWithWatch(), sink: sink, snapshotFactory: () => null, directory: NewTempDir());

        vm.StartRecordCommand.CanExecute(null).Should().BeFalse("D5 门禁：采集未运行禁用开始记录");
        vm.StartRecordCommand.ExecuteAsync(null).IsCompleted.Should().BeTrue();
        sink.IsRecording.Should().BeFalse();
    }

    [Fact]
    public async Task Start_uses_watch_list_as_channels_and_records_samples()
    {
        var sink = Sink(NewTempDir());
        var cards = CardsWithWatch();
        var snapshot = new ContractSnapshot(1, "AB", new A2lFingerprint(1, 0, 0, 0, 0), "0.1.1", []);
        var vm = new XcpRecordPanelViewModel(
            cards: cards, sink: sink, snapshotFactory: () => snapshot, directory: NewTempDir());

        vm.StartRecordCommand.CanExecute(null).Should().BeFalse("采集未运行");
        var acquisition = new XcpAcquisitionPanelViewModel { IsAcquiring = true };
        var vm2 = new XcpRecordPanelViewModel(
            acquisition: acquisition, cards: cards, sink: sink,
            snapshotFactory: () => snapshot, directory: NewTempDir());

        vm2.StartRecordCommand.CanExecute(null).Should().BeTrue();
        await vm2.StartRecordCommand.ExecuteAsync(null);

        sink.IsRecording.Should().BeTrue();
        sink.FilePath.Should().NotBeNull();

        // 样本经 sink 落盘（消费线程异步，轮询等待）。
        sink.OnValues(Sample(123.5));
        await WaitUntilAsync(() => sink.WrittenCount == 1);
        vm2.RefreshState();
        vm2.WrittenCount.Should().Be(1);
        vm2.IsRecording.Should().BeTrue();

        await sink.StopAsync();
        System.IO.File.Exists(sink.FilePath!).Should().BeTrue();
        vm2.FaultText.Should().BeNull();
    }

    [Fact]
    public async Task Acquisition_stop_hook_stops_recording_first()
    {
        var sink = Sink(NewTempDir());
        var acquisition = new XcpAcquisitionPanelViewModel { IsAcquiring = true };
        var vm2 = new XcpRecordPanelViewModel(
            acquisition: acquisition, cards: CardsWithWatch(), sink: sink,
            snapshotFactory: () => null, directory: NewTempDir());

        await vm2.StartRecordCommand.ExecuteAsync(null);
        sink.IsRecording.Should().BeTrue();

        // 采集 Stop 钩子（组合根接线到 BeforeStopAsync 的同一口）。
        await vm2.StopBeforeAcquisitionAsync();

        sink.IsRecording.Should().BeFalse();
        vm2.IsRecording.Should().BeFalse();
        vm2.StatusText.Should().Contain("随采集停止");
        System.IO.File.Exists(sink.FilePath!).Should().BeTrue();
    }

    [Fact]
    public async Task Writer_fault_sets_red_status_and_self_stops()
    {
        var dir = NewTempDir();
        var sink = new XcpMdfRecordSink(new XcpMdfRecordSinkOptions
        {
            Directory = dir,
            Channels = [new MdfChannelSpec("EngineSpeed", "rpm")],
            WriterFactory = (_, _) => new ExplodingWriter(),
        });
        var acquisition = new XcpAcquisitionPanelViewModel { IsAcquiring = true };
        var vm = new XcpRecordPanelViewModel(
            acquisition: acquisition, cards: CardsWithWatch(), sink: sink,
            snapshotFactory: () => null, directory: dir);

        await vm.StartRecordCommand.ExecuteAsync(null);
        sink.OnValues(Sample(1.0));
        await WaitUntilAsync(() => sink.IsFaulted);
        vm.RefreshState();

        vm.FaultText.Should().NotBeNull("写盘故障 → 状态区红字");
        vm.FaultText.Should().Contain("采集不受影响");
        vm.IsRecording.Should().BeFalse("记录自停");
    }

    [Fact]
    public void Empty_watch_list_blocks_start()
    {
        var sink = Sink(NewTempDir());
        var acquisition = new XcpAcquisitionPanelViewModel { IsAcquiring = true };
        var vm = new XcpRecordPanelViewModel(
            acquisition: acquisition, cards: new XcpCardPanelViewModel(), sink: sink,
            snapshotFactory: () => null, directory: NewTempDir());

        vm.StartRecordCommand.CanExecute(null).Should().BeFalse("关注集为空无可记录通道");
    }

    /// <summary>消费线程异步落的计数：轮询至条件成立（20ms 步进，2s 上限）。</summary>
    private static XcpTriggerRecordEngine TriggerEngine(string? dir = null) =>
        new(new XcpTriggerRecordOptions
        {
            Directory = dir ?? NewTempDir(),
            Channels = [new MdfChannelSpec("EngineSpeed", "rpm")],
        });

    [Fact]
    public void Trigger_is_blocked_when_acquisition_not_running()
    {
        var engine = TriggerEngine();
        var vm = new XcpRecordPanelViewModel(
            cards: CardsWithWatch(), sink: Sink(), trigger: engine, directory: NewTempDir());

        vm.TriggerRecordCommand.CanExecute(null).Should().BeFalse("D6 门禁：采集未运行禁用触发");
        vm.TriggerRecordCommand.ExecuteAsync(null).IsCompleted.Should().BeTrue();
        engine.IsCapturing.Should().BeFalse();
    }

    [Fact]
    public async Task Trigger_applies_windows_and_starts_capture_then_close_on_acquisition_stop()
    {
        var engine = TriggerEngine();
        var acquisition = new XcpAcquisitionPanelViewModel { IsAcquiring = true };
        var vm = new XcpRecordPanelViewModel(
            acquisition: acquisition, cards: CardsWithWatch(), sink: Sink(),
            trigger: engine, directory: NewTempDir());
        vm.PreTriggerSecondsText = "1";
        vm.PostTriggerSecondsText = "60"; // 长窗口保持 capturing 状态确定

        await vm.TriggerRecordCommand.ExecuteAsync(null);

        engine.IsCapturing.Should().BeTrue();
        engine.PreTriggerSeconds.Should().Be(1);
        engine.PostTriggerSeconds.Should().Be(60);
        engine.CaptureFilePath.Should().Contain("xcp_trigger_");
        vm.IsCapturing.Should().BeTrue();
        vm.StatusText.Should().Contain("触发记录已开始");

        // 采集 Stop 先关触发窗（spec D5 先停记录口径延伸到触发窗）。
        await vm.StopBeforeAcquisitionAsync();
        engine.IsCapturing.Should().BeFalse();
        System.IO.File.Exists(engine.CaptureFilePath!).Should().BeTrue();

        vm.RefreshState();
        vm.TriggerStatusText.Should().Contain("已捕获 1 次");
    }

    [Fact]
    public async Task Trigger_with_out_of_range_window_is_rejected_with_status()
    {
        var engine = TriggerEngine();
        var acquisition = new XcpAcquisitionPanelViewModel { IsAcquiring = true };
        var vm = new XcpRecordPanelViewModel(
            acquisition: acquisition, cards: CardsWithWatch(), sink: Sink(),
            trigger: engine, directory: NewTempDir());
        vm.PreTriggerSecondsText = "61"; // Q2 硬顶

        await vm.TriggerRecordCommand.ExecuteAsync(null);

        engine.IsCapturing.Should().BeFalse();
        engine.PreTriggerSeconds.Should().Be(10, "配置拒绝时保持原值");
        vm.StatusText.Should().Contain("触发窗口配置拒绝");
        vm.TriggerRecordCommand.CanExecute(null).Should().BeTrue("拒绝后仍可修正再触发");
    }

    private static async System.Threading.Tasks.Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
            await System.Threading.Tasks.Task.Delay(20);
        condition().Should().BeTrue("2 s 内条件未成立");
    }

    /// <summary>故障注入写入器（App 侧本地桩；Core.Tests 的桩跨程序集不可见）。</summary>
    private sealed class ExplodingWriter : IMdfRecordWriter
    {
        public long RecordCount => 0;

        public Task WriteRecordAsync(int channelIndex, double timeSeconds, double value, CancellationToken ct = default) =>
            throw new IOiationException();

        public Task WriteInvalidRecordAsync(int channelIndex, double timeSeconds, CancellationToken ct = default) =>
            throw new IOiationException();

        public Task WriteGapEventAsync(double timeSeconds, string kind, string cause, string detail,
            string receiveKind, double expectedMaxSeconds, CancellationToken ct = default) =>
            throw new IOiationException();

        public Task WriteAttachmentAsync(string mimeType, string comment, ReadOnlyMemory<byte> data, CancellationToken ct = default) =>
            throw new IOiationException();

        public Task FinalizeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class IOiationException : System.IO.IOException
        {
            public IOiationException() : base("disk exploded") { }
        }
    }
}
