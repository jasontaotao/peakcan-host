using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core.Dbc;
using PeakCan.Host.Core.Replay;
using Xunit;
using FrameFlags = PeakCan.Host.Core.FrameFlags;
using ValueType = PeakCan.Host.Core.Dbc.ValueType;

namespace PeakCan.Host.App.Tests.ViewModels;

public class TraceViewerViewModelTests
{
    private static ITraceViewerService MakeFakeService() => Substitute.For<ITraceViewerService>();

    private static ILogger<TraceViewerViewModel> MakeFakeLogger()
        => Substitute.For<ILogger<TraceViewerViewModel>>();

    // TBD-2: substitute the concrete DbcService via NSubstitute's
    // constructor pattern. The production ctor accepts DbcService
    // directly (not an interface) — partial + virtual methods let
    // NSubstitute intercept LoadAsync without touching the disk.
    private static DbcService MakeFakeDbcService()
        => Substitute.For<DbcService>(Substitute.For<ILogger<DbcService>>());

    // ===== v3.0.1 PATCH Task 2 fixtures =====

    // One CAN ID (0x100), one unsigned 16-bit little-endian signal at
    // startBit 0. factor=1, offset=0 → raw 16-bit value == decoded value.
    private static DbcDocument DocWithRpmSignal() => DocWithSignals(
        id: 0x100,
        name: "M_RPM",
        signals: new[]
        {
            new Signal(
                Name: "RPM", StartBit: 0, Length: 16,
                Order: ByteOrder.LittleEndian,
                ValueType: ValueType.Unsigned,
                Factor: 1.0, Offset: 0.0,
                Min: 0, Max: 1000, Unit: "rpm",
                Receivers: System.Array.Empty<string>()),
        });

    // Same ID (0x100), two signals: RPM (0-15 LE) + TEMP (16-31 LE).
    private static DbcDocument DocWithRpmAndTemp() => DocWithSignals(
        id: 0x100,
        name: "M_ENGINE",
        signals: new[]
        {
            new Signal(
                Name: "RPM", StartBit: 0, Length: 16,
                Order: ByteOrder.LittleEndian,
                ValueType: ValueType.Unsigned,
                Factor: 1.0, Offset: 0.0,
                Min: 0, Max: 1000, Unit: "rpm",
                Receivers: System.Array.Empty<string>()),
            new Signal(
                Name: "TEMP", StartBit: 16, Length: 16,
                Order: ByteOrder.LittleEndian,
                ValueType: ValueType.Unsigned,
                Factor: 1.0, Offset: 0.0,
                Min: -50, Max: 200, Unit: "C",
                Receivers: System.Array.Empty<string>()),
        });

    private static DbcDocument DocWithSignals(
        uint id, string name, IReadOnlyList<Signal> signals)
    {
        var msg = new Message(
            Id: id, Name: name, Dlc: 8, Sender: "ECU",
            Signals: signals,
            IsMultiplexed: false, MultiplexorSignalIndex: null);
        var dict = new Dictionary<uint, Message> { [id] = msg };
        return new DbcDocument(
            Version: "",
            Nodes: System.Array.Empty<Node>(),
            Messages: new[] { msg },
            MessagesById: dict,
            ValueTables: new Dictionary<string, ValueTable>());
    }

    private static ReplayFrame Frame(uint id, params byte[] data) =>
        new(Timestamp: 0.0, Id: id, Dlc: (byte)data.Length, Data: data, Flags: FrameFlags.None);

    [Fact]
    public void Ctor_Empty_NoSignalsNoCharts()
    {
        var sut = new TraceViewerViewModel(MakeFakeService(), MakeFakeDbcService(), MakeFakeLogger());
        sut.Signals.Should().BeEmpty();
        sut.ChartViewModel.Series.Should().BeEmpty();
    }

    [Fact]
    public async Task OpenFileAsync_InvokesServiceLoadAsync()
    {
        var svc = MakeFakeService();
        var sut = new TraceViewerViewModel(svc, MakeFakeDbcService(), MakeFakeLogger());
        await sut.OpenFileAsync("C:/fake.asc");
        await svc.Received(1).LoadAsync("C:/fake.asc", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void PlayCommand_InvokesServicePlay()
    {
        var svc = MakeFakeService();
        var sut = new TraceViewerViewModel(svc, MakeFakeDbcService(), MakeFakeLogger());
        sut.PlayCommand.Execute(null);
        svc.Received(1).Play();
    }

    [Fact]
    public void PauseCommand_InvokesServicePause()
    {
        var svc = MakeFakeService();
        var sut = new TraceViewerViewModel(svc, MakeFakeDbcService(), MakeFakeLogger());
        sut.PauseCommand.Execute(null);
        svc.Received(1).Pause();
    }

    [Fact]
    public void StopCommand_InvokesServiceStop()
    {
        var svc = MakeFakeService();
        var sut = new TraceViewerViewModel(svc, MakeFakeDbcService(), MakeFakeLogger());
        sut.StopCommand.Execute(null);
        svc.Received(1).Stop();
    }

    // ===== v3.0.1 PATCH Task 2: per-signal DBC decode =====

    [Fact]
    public async Task RebuildSignalsAsync_NoDbc_LeavesSignalsEmpty()
    {
        var svc = MakeFakeService();
        // Frames present, but the service cannot decode without a DBC.
        svc.LoadedFrames.Returns(new[] { Frame(0x100, 0x42, 0x00) });
        // No DBC set — DbcService.Current remains null.
        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        var sut = new TraceViewerViewModel(svc, dbc, MakeFakeLogger());

        await sut.OpenFileAsync("C:/fake.asc");

        sut.Signals.Should().BeEmpty();
    }

    [Fact]
    public async Task RebuildSignalsAsync_DbcLoaded_PopulatesOneRowPerSignal()
    {
        var svc = MakeFakeService();
        // Two frames for 0x100 → LatestValue is the last decoded value.
        // RPM is unsigned LE 16-bit @ startBit 0, factor=1 → bytes [0x42,0x01] = 0x0142 = 322.
        svc.LoadedFrames.Returns(new[]
        {
            Frame(0x100, 0x00, 0x00),         // 0
            Frame(0x100, 0x42, 0x01),         // 322
        });
        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        dbc.SetCurrentForTests(DocWithRpmSignal());
        var sut = new TraceViewerViewModel(svc, dbc, MakeFakeLogger());

        await sut.OpenFileAsync("C:/fake.asc");

        sut.Signals.Should().HaveCount(1);
        var row = sut.Signals[0];
        row.CanIdHex.Should().Be("0x100");
        row.SignalName.Should().Be("RPM");
        row.Unit.Should().Be("rpm");
        row.IsPlotted.Should().BeFalse();
        row.LatestValue.Should().Be(322.0);
    }

    [Fact]
    public async Task RebuildSignalsAsync_MultipleSignalsSameId_PopulatesAll()
    {
        var svc = MakeFakeService();
        // bytes [0x10,0x00] = RPM 0x0010 = 16; bytes [0x20,0x00] = TEMP 0x0020 = 32.
        svc.LoadedFrames.Returns(new[]
        {
            Frame(0x100, 0x10, 0x00, 0x20, 0x00),
        });
        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        dbc.SetCurrentForTests(DocWithRpmAndTemp());
        var sut = new TraceViewerViewModel(svc, dbc, MakeFakeLogger());

        await sut.OpenFileAsync("C:/fake.asc");

        sut.Signals.Should().HaveCount(2);
        sut.Signals[0].SignalName.Should().Be("RPM");
        sut.Signals[0].LatestValue.Should().Be(16.0);
        sut.Signals[1].SignalName.Should().Be("TEMP");
        sut.Signals[1].LatestValue.Should().Be(32.0);
    }

    [Fact]
    public async Task RebuildSignalsAsync_NoMatchingFrames_LeavesSignalsEmpty()
    {
        var svc = MakeFakeService();
        // DBC defines id 0x100, but only id 0x555 frames are loaded.
        svc.LoadedFrames.Returns(new[]
        {
            Frame(0x555, 0x42, 0x00),
        });
        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        dbc.SetCurrentForTests(DocWithRpmSignal());
        var sut = new TraceViewerViewModel(svc, dbc, MakeFakeLogger());

        await sut.OpenFileAsync("C:/fake.asc");

        sut.Signals.Should().BeEmpty();
    }

    [Fact]
    public async Task RebuildSignalsAsync_LatestValueIsLastDecoded()
    {
        var svc = MakeFakeService();
        // Three frames for 0x100 → LatestValue must be the LAST decoded,
        // not the first nor the max. RPM 16-bit LE unsigned:
        //   frame1: 0x01,0x00 → 1
        //   frame2: 0xFF,0x00 → 255  (max)
        //   frame3: 0x05,0x00 → 5    (last — this is the asserted value)
        svc.LoadedFrames.Returns(new[]
        {
            Frame(0x100, 0x01, 0x00),
            Frame(0x100, 0xFF, 0x00),
            Frame(0x100, 0x05, 0x00),
        });
        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        dbc.SetCurrentForTests(DocWithRpmSignal());
        var sut = new TraceViewerViewModel(svc, dbc, MakeFakeLogger());

        await sut.OpenFileAsync("C:/fake.asc");

        sut.Signals.Should().HaveCount(1);
        sut.Signals[0].LatestValue.Should().Be(5.0);
    }
}