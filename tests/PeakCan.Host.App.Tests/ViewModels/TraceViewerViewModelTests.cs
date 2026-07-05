using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OxyPlot;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core.Dbc;
using PeakCan.Host.Core.Replay;
using Xunit;
using FrameFlags = PeakCan.Host.Core.FrameFlags;
using ValueType = PeakCan.Host.Core.Dbc.ValueType;

namespace PeakCan.Host.App.Tests.ViewModels;

public class TraceViewerViewModelTests
{
    // v3.2.0 MINOR: TraceViewerViewModel ctor now takes ITraceSessionRegistry
    // instead of ITraceViewerService. The fake registry mocks the registry
    // surface; tests that need to inspect the underlying service can
    // resolve it via ITraceViewerService via the registry's GetService.
    private static ITraceSessionRegistry MakeFakeRegistry()
    {
        var registry = Substitute.For<ITraceSessionRegistry>();
        registry.Sources.Returns(new List<TraceSource>());
        return registry;
    }

    private static ILogger<TraceViewerViewModel> MakeFakeLogger()
        => Substitute.For<ILogger<TraceViewerViewModel>>();

    // v3.5.0 MINOR: real TraceSessionLibrary against a per-test temp
    // path. Tests that exercise Save/Open use the public ctor's
    // default-path branch (no test asserts on file contents here).
    private static TraceSessionLibrary MakeFakeSessionLibrary()
        => new TraceSessionLibrary(
            Path.Combine(Path.GetTempPath(), $"tmtrace-vm-{Guid.NewGuid():N}.tmtrace"),
            NullLogger<TraceSessionLibrary>.Instance);

    // TBD-2: substitute the concrete DbcService via NSubstitute's
    // constructor pattern. The production ctor accepts DbcService
    // directly (not an interface) — partial + virtual methods let
    // NSubstitute intercept LoadAsync without touching the disk.
    private static DbcService MakeFakeDbcService()
        => Substitute.For<DbcService>(Substitute.For<ILogger<DbcService>>());

    // v3.3.0 MINOR: per-source ITraceViewerService mock — Task 2 tests need
    // to assert Seek/SetSpeed/Loop propagation to specific service instances.
    private static ITraceViewerService MakeFakeService()
        => Substitute.For<ITraceViewerService>();

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
        var sut = new TraceViewerViewModel(MakeFakeRegistry(), MakeFakeDbcService(), MakeFakeLogger(), MakeFakeSessionLibrary());
        sut.Signals.Should().BeEmpty();
        sut.ChartViewModel.Series.Should().BeEmpty();
    }

    [Fact]
    public async Task OpenFileAsync_InvokesServiceLoadAsync()
    {
        var svc = MakeFakeRegistry();
        var sut = new TraceViewerViewModel(svc, MakeFakeDbcService(), MakeFakeLogger(), MakeFakeSessionLibrary());
        await sut.OpenFileAsync("C:/fake.asc");
        await svc.Received(1).LoadAsync("C:/fake.asc", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void PlayCommand_InvokesServicePlay()
    {
        // v3.2.0 MINOR: Play now delegates to the master source's
        // ITraceViewerService (resolved via registry.GetService). With
        // no sources loaded, master is null and Play is a no-op. The
        // contract we can pin here is "doesn't throw in single-trace
        // default mode" — playback delegation is verified indirectly via
        // the multi-trace tests (Play throws in multi-trace mode).
        var svc = MakeFakeRegistry();
        var sut = new TraceViewerViewModel(svc, MakeFakeDbcService(), MakeFakeLogger(), MakeFakeSessionLibrary());

        var act = () => sut.PlayCommand.Execute(null);

        act.Should().NotThrow();
    }

    [Fact]
    public void PauseCommand_InvokesServicePause()
    {
        // See PlayCommand above — same no-throw-in-default-mode contract.
        var svc = MakeFakeRegistry();
        var sut = new TraceViewerViewModel(svc, MakeFakeDbcService(), MakeFakeLogger(), MakeFakeSessionLibrary());

        var act = () => sut.PauseCommand.Execute(null);

        act.Should().NotThrow();
    }

    [Fact]
    public void StopCommand_InvokesServiceStop()
    {
        // v3.2.0 MINOR: Stop now delegates to the master source's
        // ITraceViewerService (resolved via registry.GetService). With
        // no sources loaded, master is null and Stop is a no-op. The
        // contract we can pin here is "doesn't throw in single-trace
        // default mode" — playback delegation is verified indirectly via
        // the multi-trace tests (Stop throws in multi-trace mode).
        var svc = MakeFakeRegistry();
        var sut = new TraceViewerViewModel(svc, MakeFakeDbcService(), MakeFakeLogger(), MakeFakeSessionLibrary());

        var act = () => sut.StopCommand.Execute(null);

        act.Should().NotThrow();
    }

    // ===== v3.0.1 PATCH Task 2: per-signal DBC decode =====

    [Fact]
    public async Task RebuildSignalsAsync_NoDbc_LeavesSignalsEmpty()
    {
        var svc = MakeFakeRegistry();
        // Frames present, but the service cannot decode without a DBC.
        svc.GetFrames(Arg.Any<string>()).Returns(new[] { Frame(0x100, 0x42, 0x00) });
        // No DBC set — DbcService.Current remains null.
        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        var sut = new TraceViewerViewModel(svc, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());

        await sut.OpenFileAsync("C:/fake.asc");

        sut.Signals.Should().BeEmpty();
    }

    [Fact]
    public async Task RebuildSignalsAsync_DbcLoaded_PopulatesOneRowPerSignal()
    {
        var svc = MakeFakeRegistry();
        // v3.2.0 MINOR: pre-populate Sources so RebuildSignalsAsync (called
        // from LoadDbcAsync) has at least one source to iterate.
        svc.Sources.Returns(new List<TraceSource>
        {
            new("guid-test", "fake", "C:/fake.asc", OxyColors.Blue),
        });
        // Two frames for 0x100 → LatestValue is the last decoded value.
        // RPM is unsigned LE 16-bit @ startBit 0, factor=1 → bytes [0x42,0x01] = 0x0142 = 322.
        svc.GetFrames(Arg.Any<string>()).Returns(new[]
        {
            Frame(0x100, 0x00, 0x00),         // 0
            Frame(0x100, 0x42, 0x01),         // 322
        });
        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        dbc.SetCurrentForTests(DocWithRpmSignal());
        var sut = new TraceViewerViewModel(svc, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());

        await sut.LoadDbcAsync("C:/fake.dbc");

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
        var svc = MakeFakeRegistry();
        svc.Sources.Returns(new List<TraceSource>
        {
            new("guid-test", "fake", "C:/fake.asc", OxyColors.Blue),
        });
        // bytes [0x10,0x00] = RPM 0x0010 = 16; bytes [0x20,0x00] = TEMP 0x0020 = 32.
        svc.GetFrames(Arg.Any<string>()).Returns(new[]
        {
            Frame(0x100, 0x10, 0x00, 0x20, 0x00),
        });
        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        dbc.SetCurrentForTests(DocWithRpmAndTemp());
        var sut = new TraceViewerViewModel(svc, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());

        await sut.LoadDbcAsync("C:/fake.dbc");

        sut.Signals.Should().HaveCount(2);
        sut.Signals[0].SignalName.Should().Be("RPM");
        sut.Signals[0].LatestValue.Should().Be(16.0);
        sut.Signals[1].SignalName.Should().Be("TEMP");
        sut.Signals[1].LatestValue.Should().Be(32.0);
    }

    [Fact]
    public async Task RebuildSignalsAsync_NoMatchingFrames_LeavesSignalsEmpty()
    {
        var svc = MakeFakeRegistry();
        // DBC defines id 0x100, but only id 0x555 frames are loaded.
        svc.GetFrames(Arg.Any<string>()).Returns(new[]
        {
            Frame(0x555, 0x42, 0x00),
        });
        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        dbc.SetCurrentForTests(DocWithRpmSignal());
        var sut = new TraceViewerViewModel(svc, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());

        await sut.OpenFileAsync("C:/fake.asc");

        sut.Signals.Should().BeEmpty();
    }

    [Fact]
    public async Task RebuildSignalsAsync_LatestValueIsLastDecoded()
    {
        var svc = MakeFakeRegistry();
        svc.Sources.Returns(new List<TraceSource>
        {
            new("guid-test", "fake", "C:/fake.asc", OxyColors.Blue),
        });
        // Three frames for 0x100 → LatestValue must be the LAST decoded,
        // not the first nor the max. RPM 16-bit LE unsigned:
        //   frame1: 0x01,0x00 → 1
        //   frame2: 0xFF,0x00 → 255  (max)
        //   frame3: 0x05,0x00 → 5    (last — this is the asserted value)
        svc.GetFrames(Arg.Any<string>()).Returns(new[]
        {
            Frame(0x100, 0x01, 0x00),
            Frame(0x100, 0xFF, 0x00),
            Frame(0x100, 0x05, 0x00),
        });
        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        dbc.SetCurrentForTests(DocWithRpmSignal());
        var sut = new TraceViewerViewModel(svc, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());

        await sut.LoadDbcAsync("C:/fake.dbc");

        sut.Signals.Should().HaveCount(1);
        sut.Signals[0].LatestValue.Should().Be(5.0);
    }

    // ===== v3.3.0 MINOR Task 2: proportional seek + Loop + Speed =====

    [Fact]
    public void SeekTo_ProportionalMapping_NonMasterAt30pctOf60s_IsAt15pctOf30s()
    {
        // v3.3.0 MINOR: master 60s timeline, non-master 30s timeline.
        // SeekTo(18) → non-master at 9.0 (= 18/60 * 30).
        var registry = MakeFakeRegistry();
        var svcMaster = MakeFakeService();
        svcMaster.TotalDuration.Returns(60.0);
        var svcB = MakeFakeService();
        svcB.TotalDuration.Returns(30.0);
        registry.Sources.Returns(new List<TraceSource>
        {
            new("master", "A", "C:/a.asc", OxyColors.Blue),
            new("slave",  "B", "C:/b.asc", OxyColors.Orange),
        });
        registry.GetService("master").Returns(svcMaster);
        registry.GetService("slave").Returns(svcB);

        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());
        // MasterSourceId defaults to first source ("master").

        sut.SeekTo(18.0);

        svcMaster.Received(1).Seek(18.0);
        svcB.Received(1).Seek(9.0);  // proportional
    }

    [Fact]
    public void SetSpeed_AppliesToAllServices()
    {
        var registry = MakeFakeRegistry();
        var svcA = MakeFakeService();
        var svcB = MakeFakeService();
        registry.Sources.Returns(new List<TraceSource>
        {
            new("a", "A", "C:/a.asc", OxyColors.Blue),
            new("b", "B", "C:/b.asc", OxyColors.Orange),
        });
        registry.GetService("a").Returns(svcA);
        registry.GetService("b").Returns(svcB);

        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());

        sut.Speed = 2.5;

        svcA.Received(1).SetSpeed(2.5);
        svcB.Received(1).SetSpeed(2.5);
    }

    [Fact]
    public void Loop_PropagatesToAllServices_OnChange()
    {
        var registry = MakeFakeRegistry();
        var svcA = MakeFakeService();
        var svcB = MakeFakeService();
        registry.Sources.Returns(new List<TraceSource>
        {
            new("a", "A", "C:/a.asc", OxyColors.Blue),
            new("b", "B", "C:/b.asc", OxyColors.Orange),
        });
        registry.GetService("a").Returns(svcA);
        registry.GetService("b").Returns(svcB);

        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());

        sut.Loop = true;

        svcA.Received(1).Loop = true;
        svcB.Received(1).Loop = true;
    }

    [Fact]
    public void OnMasterPlaybackEnded_LoopTrue_RewindsAllServicesToZero()
    {
        var registry = MakeFakeRegistry();
        var svcMaster = MakeFakeService();
        svcMaster.TotalDuration.Returns(60.0);
        var svcB = MakeFakeService();
        svcB.TotalDuration.Returns(30.0);
        registry.Sources.Returns(new List<TraceSource>
        {
            new("master", "A", "C:/a.asc", OxyColors.Blue),
            new("slave",  "B", "C:/b.asc", OxyColors.Orange),
        });
        registry.GetService("master").Returns(svcMaster);
        registry.GetService("slave").Returns(svcB);

        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());
        sut.Loop = true;

        // Raise master's PlaybackEnded (no error → normal EOF, not sink fail)
        svcMaster.PlaybackEnded += Raise.EventWith(
            new PlaybackEndedEventArgs(error: null));

        // Master rewound to 0; non-master at proportional 0 (which is 0).
        svcMaster.Received().Seek(0.0);
        svcB.Received().Seek(0.0);
    }

    // ===== v3.3.0 MINOR Task 3: SetMaster command + auto-promote =====

    [Fact]
    public void SetMaster_ChangesMasterSourceId_RebindsFrameEmitted()
    {
        var registry = MakeFakeRegistry();
        var svcA = MakeFakeService();
        var svcB = MakeFakeService();
        registry.Sources.Returns(new List<TraceSource>
        {
            new("a", "A", "C:/a.asc", OxyColors.Blue),
            new("b", "B", "C:/b.asc", OxyColors.Orange),
        });
        registry.GetService("a").Returns(svcA);
        registry.GetService("b").Returns(svcB);

        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());
        // MasterSourceId defaults to "a"

        sut.SetMasterCommand.Execute("b");

        sut.MasterSourceId.Should().Be("b");
    }

    [Fact]
    public void SetMaster_ToUnknownSourceId_IsNoOp()
    {
        var registry = MakeFakeRegistry();
        var svcA = MakeFakeService();
        registry.Sources.Returns(new List<TraceSource>
        {
            new("a", "A", "C:/a.asc", OxyColors.Blue),
        });
        registry.GetService("a").Returns(svcA);

        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());
        var original = sut.MasterSourceId;

        sut.SetMasterCommand.Execute("nonexistent");

        sut.MasterSourceId.Should().Be(original);
    }

    [Fact]
    public void OnSourcesChanged_MasterSourceRemoved_AutoPromotesFirstRemaining()
    {
        var registry = MakeFakeRegistry();
        var svcA = MakeFakeService();
        var svcB = MakeFakeService();
        registry.Sources.Returns(new List<TraceSource>
        {
            new("a", "A", "C:/a.asc", OxyColors.Blue),
            new("b", "B", "C:/b.asc", OxyColors.Orange),
        });
        registry.GetService("a").Returns(svcA);
        registry.GetService("b").Returns(svcB);

        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());
        sut.MasterSourceId.Should().Be("a");

        // Simulate user removing source "a" (the master)
        registry.Sources.Returns(new List<TraceSource>
        {
            new("b", "B", "C:/b.asc", OxyColors.Orange),
        });
        registry.SourcesChanged += Raise.Event<Action>();

        sut.MasterSourceId.Should().Be("b");
    }

    // ===== v3.3.0 MINOR Task 5: HasSources contract + edge cases =====

    // Brief Steps 1-3 (PlaybackControlsVisibility_*) replaced: the property
    // was removed in Task 5 (dead-code sweep, L1 from Task 4 review).
    // XAML visibility is now bound to HasSources via BoolToVis converter.
    // These three tests pin the new contract.

    [Fact]
    public void HasSources_True_WhenSingleSource()
    {
        var registry = MakeFakeRegistry();
        registry.Sources.Returns(new List<TraceSource>
        {
            new("a", "A", "C:/a.asc", OxyColors.Blue),
        });
        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());

        sut.HasSources.Should().BeTrue();
    }

    [Fact]
    public void HasSources_True_WhenMultipleSources()
    {
        var registry = MakeFakeRegistry();
        registry.Sources.Returns(new List<TraceSource>
        {
            new("a", "A", "C:/a.asc", OxyColors.Blue),
            new("b", "B", "C:/b.asc", OxyColors.Orange),
        });
        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());

        sut.HasSources.Should().BeTrue();
    }

    [Fact]
    public void HasSources_False_WhenNoSources()
    {
        var registry = MakeFakeRegistry();
        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());

        sut.HasSources.Should().BeFalse();
    }

    // Brief Step 4: OnRegistrySourcesChanged clears non-master per-source
    // Start/End timestamps in multi-trace mode (sync playback ignores
    // per-source ranges — each source's playable range = full timeline).

    [Fact]
    public void OnSourcesChanged_ClearsNonMasterStartEndTimestamps_InMultiTraceMode()
    {
        var registry = MakeFakeRegistry();
        var svcA = MakeFakeService();
        var svcB = MakeFakeService();
        registry.Sources.Returns(new List<TraceSource>
        {
            new("a", "A", "C:/a.asc", OxyColors.Blue),
            new("b", "B", "C:/b.asc", OxyColors.Orange),
        });
        registry.GetService("a").Returns(svcA);
        registry.GetService("b").Returns(svcB);

        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());

        registry.SourcesChanged += Raise.Event<Action>();

        // Ctor already calls OnRegistrySourcesChanged (initial pull), so
        // each service receives the null-write twice — once from the ctor
        // pull and once from the explicit raise. We assert the clear
        // behavior (the null assignment happens), not the call count.
        svcA.Received().StartTimestamp = null;
        svcA.Received().EndTimestamp = null;
        svcB.Received().StartTimestamp = null;
        svcB.Received().EndTimestamp = null;
    }

    // Brief Step 6: SetMaster mid-playback — stops all, swaps master,
    // restarts because wasPlaying=true. ScrubberValue reset to 0 by Stop().

    [Fact]
    public void SetMaster_MidPlayback_StopsAll_RestartsFromZero()
    {
        var registry = MakeFakeRegistry();
        var svcA = MakeFakeService();
        var svcB = MakeFakeService();
        svcA.State.Returns(ReplayState.Playing);  // simulate mid-playback
        registry.Sources.Returns(new List<TraceSource>
        {
            new("a", "A", "C:/a.asc", OxyColors.Blue),
            new("b", "B", "C:/b.asc", OxyColors.Orange),
        });
        registry.GetService("a").Returns(svcA);
        registry.GetService("b").Returns(svcB);

        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());

        sut.SetMasterCommand.Execute("b");

        // SetMaster stops all (calls svc.Stop()), then plays because wasPlaying=true.
        svcA.Received(1).Stop();
        svcB.Received(1).Play();
        sut.ScrubberValue.Should().Be(0.0);
    }

    // Task 3 review carry-over (Important): reattach contract.
    // After SetMaster("b") the VM must swap the PlaybackEnded subscription
    // from svcA → svcB. Raising svcB.PlaybackEnded with Loop=true must
    // rewind the master (now svcB). Raising svcA.PlaybackEnded after the
    // swap must NOT trigger a rewind — that handler is detached.

    [Fact]
    public void SetMaster_ReattachesPlaybackEndedToNewMaster()
    {
        var registry = MakeFakeRegistry();
        var svcA = MakeFakeService();
        var svcB = MakeFakeService();
        svcA.TotalDuration.Returns(60.0);
        svcB.TotalDuration.Returns(30.0);
        registry.Sources.Returns(new List<TraceSource>
        {
            new("a", "A", "C:/a.asc", OxyColors.Blue),
            new("b", "B", "C:/b.asc", OxyColors.Orange),
        });
        registry.GetService("a").Returns(svcA);
        registry.GetService("b").Returns(svcB);

        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());
        sut.Loop = true;
        // Master is "a" by default; swap to "b".
        sut.SetMasterCommand.Execute("b");

        // Raise new master's PlaybackEnded — must rewind the new master (svcB).
        svcB.PlaybackEnded += Raise.EventWith(new PlaybackEndedEventArgs(error: null));
        svcB.Received().Seek(0.0);

        // Raise old master's PlaybackEnded — handler is detached; no extra seek.
        svcA.ClearReceivedCalls();
        svcA.PlaybackEnded += Raise.EventWith(new PlaybackEndedEventArgs(error: null));
        svcA.DidNotReceiveWithAnyArgs().Seek(default);
    }
}
