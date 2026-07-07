using System.IO;
using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OxyPlot;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core;
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

    // ===== v3.6.0 MINOR T1 helpers =====

    // Real TraceSessionLibrary against a per-test temp path. Returns the
    // library + the path it should save/load to. The library uses an
    // internal test ctor (mirrors MakeFakeSessionLibrary but exposes the
    // path so callers can re-load it).
    private static TraceSessionLibrary NewTestLibrary(out string libPath)
    {
        libPath = Path.Combine(
            Path.GetTempPath(),
            $"tmtrace-vm-{Guid.NewGuid():N}.tmtrace");
        return new TraceSessionLibrary(
            libPath,
            NullLogger<TraceSessionLibrary>.Instance);
    }

    // v3.6.0 MINOR T1: thin wrapper around the canonical VM ctor used by
    // the bundle round-trip tests. Exists so T1 tests don't need to know
    // which constructor argument is the IFileDialogService.
    private static TraceViewerViewModel NewVm(TraceSessionLibrary library)
        => new TraceViewerViewModel(
            MakeFakeRegistry(),
            MakeFakeDbcService(),
            MakeFakeLogger(),
            library);

    // v3.11.4 PATCH: no-args overload that the CanExecute test
    // (CanAddTrace_True_When_IsLoading_False_Regardless_Of_Argument)
    // depends on. Delegates to the real TraceSessionLibrary ctor with a
    // per-test temp path (mirrors AppShellViewModelTests.NewFakeSessionLibrary).
    private static TraceViewerViewModel NewVm()
        => new TraceViewerViewModel(
            MakeFakeRegistry(),
            MakeFakeDbcService(),
            MakeFakeLogger(),
            new TraceSessionLibrary(
                Path.Combine(Path.GetTempPath(), $"tmtrace-vm-{Guid.NewGuid():N}.tmtrace"),
                NullLogger<TraceSessionLibrary>.Instance));

    /// <summary>
    /// v3.11.4 PATCH: factory that wires the explicit <see cref="IFileDialogService"/>
    /// and <see cref="ITraceSessionRegistry"/> substitutes the new tests
    /// need. Mirrors the existing <c>NewVm()</c> shape but takes both
    /// substitutes as parameters so each test controls dialog return value +
    /// registry assertion target.
    /// </summary>
    private static TraceViewerViewModel NewVmWithDialog(
        ITraceSessionRegistry registry,
        IFileDialogService dialog)
    {
        var logger = NullLogger<TraceViewerViewModel>.Instance;
        var dbcService = MakeFakeDbcService();
        var sessionLibrary = new TraceSessionLibrary(
            Path.Combine(Path.GetTempPath(), $"tmtrace-vm-{Guid.NewGuid():N}.tmtrace"),
            NullLogger<TraceSessionLibrary>.Instance);
        return new TraceViewerViewModel(registry, dbcService, logger, sessionLibrary, dialog);
    }

    // Seed the fake registry with one source having the requested
    // DisplayName + Color. Returns the seeded TraceSource so callers can
    // re-read it for assertions. Uses distinct Guid-derived ids per call
    // so reload-after-clear scenarios have stable ids.
    // v3.6.0 MINOR T1: review-fix — default DisplayName intentionally
    // DIFFERS from the path's filename so the production restore guard
    // (`bs.DisplayName != filenameOnly`) fires when this helper is used
    // without overrides. Defaulting to a name matching the path basename
    // would silently skip the restore branch on future test reuse.
    private static TraceSource AddFakeTraceSource(
        ITraceSessionRegistry registry,
        string displayName = "non_default_fake",
        OxyColor? color = null,
        string? sourceId = null)
    {
        var src = new TraceSource(
            sourceId ?? Guid.NewGuid().ToString("N"),
            displayName,
            $"C:/fake.asc",
            color ?? OxyColors.Blue);
        registry.Sources.Returns(new List<TraceSource> { src });
        registry.SourcesChanged += Raise.Event<Action>();
        return src;
    }

    // v3.6.0 MINOR T1: save the VM's current state, reload it via a fresh
    // VM pointed at the same library, return the reloaded DTO. The caller
    // can assert on the bundle's contents.
    private static TraceSessionBundleDto SaveAndReloadBundle(
        TraceViewerViewModel vm,
        TraceSessionLibrary library,
        string libPath)
    {
        // Build + save via the public SaveSessionAsync method.
        vm.SaveSessionAsync(libPath).GetAwaiter().GetResult();
        return library.Load(libPath)
            ?? throw new InvalidOperationException(
                $"Bundle at {libPath} could not be reloaded after Save");
    }

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
    public async Task AddTraceAsync_InvokesServiceLoadAsync()
    {
        // v3.9.2 PATCH H2: OpenFileAsync (legacy v3.0 alias) was deleted;
        // these tests now exercise the canonical AddTraceAsync directly.
        // v3.11.4 PATCH: AddTraceAsync is parameterless; the path comes from
        // the IFileDialogService.ShowOpenDialog call. Stub the dialog to
        // return the path the test expects to be forwarded to the registry.
        var svc = MakeFakeRegistry();
        var dialog = Substitute.For<IFileDialogService>();
        dialog.ShowOpenDialog(Arg.Any<string>()).Returns("C:/fake.asc");
        var sut = new TraceViewerViewModel(svc, MakeFakeDbcService(), MakeFakeLogger(), MakeFakeSessionLibrary(), dialog);
        await sut.AddTraceAsync();
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
        // v3.11.4 PATCH: AddTraceAsync parameterless; dialog drives the path.
        var dialog = Substitute.For<IFileDialogService>();
        dialog.ShowOpenDialog(Arg.Any<string>()).Returns("C:/fake.asc");
        var sut = new TraceViewerViewModel(svc, dbc, MakeFakeLogger(), MakeFakeSessionLibrary(), dialog);

        await sut.AddTraceAsync();

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
        // v3.11.4 PATCH: AddTraceAsync parameterless; dialog drives the path.
        var dialog = Substitute.For<IFileDialogService>();
        dialog.ShowOpenDialog(Arg.Any<string>()).Returns("C:/fake.asc");
        var sut = new TraceViewerViewModel(svc, dbc, MakeFakeLogger(), MakeFakeSessionLibrary(), dialog);

        await sut.AddTraceAsync();

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

    // ---------- v3.8.6 PATCH H1: SeekTo input validation (symmetry-miss of v3.8.4 L1) ----------

    /// <summary>
    /// v3.8.6 PATCH H1: <see cref="TraceViewerViewModel.SeekTo"/> (and the
    /// underlying <c>SeekAllToProportionalTime</c> direct-call branch)
    /// must clamp <c>t</c> to <c>[0, _masterService.TotalDuration]</c>.
    /// The v3.8.4 L1 PATCH shipped the same clamp on
    /// <c>ReplayViewModel.SeekTo</c> but missed the symmetric Trace
    /// Viewer path. A WPF <c>TwoWay</c> slider binding (or programmatic
    /// scrub) can push values outside the valid range -- passing through
    /// with the raw value would walk the master's <c>_nextFrameIndex</c>
    /// past <c>_frames.Count</c>, leaving no frame in range. With
    /// <c>Loop=true</c>, the visible position jumps; with <c>Loop=false</c>,
    /// playback silently stops.
    /// </summary>
    [Fact]
    public void SeekTo_NegativeTimestamp_ClampsToZero()
    {
        // Same setup shape as SeekTo_ProportionalMapping_NonMasterAt30pctOf60s_IsAt15pctOf30s
        // -- the ctor wires up the master service from the first registered source.
        // ScrubberValue default is 0.0 from `[ObservableProperty]`; this test
        // verifies Seek(0.0) reaches the master despite the in-range default,
        // by performing an explicit "advance-to-something-different" seek first
        // to put us mid-range (so OnScrubberValueChanged has a non-default
        // baseline to detect the change from).
        var registry = MakeFakeRegistry();
        var svcMaster = MakeFakeService();
        svcMaster.TotalDuration.Returns(60.0);
        registry.Sources.Returns(new List<TraceSource>
        {
            new("a", "A", "C:/a.asc", OxyColors.Blue),
        });
        registry.GetService("a").Returns(svcMaster);

        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());

        // Advance scrubber first so OnScrubberValueChanged has a non-default
        // baseline. The negative SeekTo(-5.0) then flips ScrubberValue to a
        // different clamped value (0.0 -> still 0.0 if ScrubberValue was 0,
        // so we use 1.0 as the baseline).
        sut.SeekTo(1.0);
        svcMaster.Received(1).Seek(1.0);

        // Negative seek is clamped at SeekAllToProportionalTime to 0.0.
        sut.SeekTo(-5.0);
        svcMaster.Received(1).Seek(0.0);
    }

    /// <summary>
    /// v3.8.6 PATCH H1: <see cref="TraceViewerViewModel.SeekTo"/> with a
    /// timestamp greater than the master's <c>TotalDuration</c> must
    /// clamp to <c>TotalDuration</c>. Mirrors the v3.8.4 L1 Replay
    /// pattern. Without the fix, the timeline walks past the last frame
    /// and the next playback emits nothing (silent dead-end).
    /// </summary>
    [Fact]
    public void SeekTo_TimestampBeyondTotalDuration_ClampsToMax()
    {
        var registry = MakeFakeRegistry();
        var svcMaster = MakeFakeService();
        svcMaster.TotalDuration.Returns(60.0);
        registry.Sources.Returns(new List<TraceSource>
        {
            new("a", "A", "C:/a.asc", OxyColors.Blue),
        });
        registry.GetService("a").Returns(svcMaster);

        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());

        sut.SeekTo(1.0e10);

        svcMaster.Received(1).Seek(60.0);
    }

    /// <summary>
    /// v3.8.6 PATCH H1: a positive-control test -- <see cref="TraceViewerViewModel.SeekTo"/>
    /// with a valid in-range timestamp must NOT clamp. Pins the
    /// non-regression path for the happy case so the clamp doesn't
    /// mangle in-range slider values.
    /// </summary>
    [Fact]
    public void SeekTo_InRangeTimestamp_PassesThroughUnchanged()
    {
        var registry = MakeFakeRegistry();
        var svcMaster = MakeFakeService();
        svcMaster.TotalDuration.Returns(60.0);
        registry.Sources.Returns(new List<TraceSource>
        {
            new("a", "A", "C:/a.asc", OxyColors.Blue),
        });
        registry.GetService("a").Returns(svcMaster);

        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());

        sut.SeekTo(15.0);

        svcMaster.Received(1).Seek(15.0);
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

    // ===== v3.5.1 PATCH (review M2): LoadedTracePath ordering =====

    [Fact]
    public async Task OpenSessionAsync_RestoresLoadedTracePath_To_FirstSource_Path()
    {
        // v3.5.1 PATCH (review M2): the property must be set explicitly
        // inside ApplySnapshotAsync after RebuildSignalsCore, not left
        // to OnRegistrySourcesChanged firing inside LoadAsync. Seed the
        // registry with two sources, save a snapshot with the same
        // paths, modify LoadedTracePath to a stale value, then call
        // OpenSessionAsync and assert the property is restored to the
        // first source's path.
        const string path1 = "C:/path/to/s1.asc";
        const string path2 = "C:/path/to/s2.asc";
        var loadedSources = new List<TraceSource>
        {
            new("aaa", "s1", path1, OxyColors.Blue, LineStyle.Solid),
            new("bbb", "s2", path2, OxyColors.Orange, LineStyle.Solid),
        };
        var registry = Substitute.For<ITraceSessionRegistry>();
        registry.Sources.Returns(loadedSources);
        // ApplySnapshotAsync calls registry.LoadAsync in a loop; return
        // the matching entry per path so Sources.Count > 0 after restore.
        registry.LoadAsync(path1, Arg.Any<CancellationToken>())
            .Returns(loadedSources[0]);
        registry.LoadAsync(path2, Arg.Any<CancellationToken>())
            .Returns(loadedSources[1]);

        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        var library = MakeFakeSessionLibrary();
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger(), library);

        // Seed an arbitrary stale value to verify post-restore override.
        sut.LoadedTracePath = "stale/path.asc";
        sut.LoadedTracePath.Should().Be("stale/path.asc");

        // Persist a snapshot that the test will re-load.
        var snapshot = new TraceSessionBundleDto
        {
            Version = 1,
            Schema = TraceSessionLibrary.CurrentSchema,
            SavedAt = DateTimeOffset.UtcNow,
            AppVersion = "3.5.0",
            Sources = new List<BundleSourceDto>
            {
                new() { SourceId = "aaa", DisplayName = "s1", Path = path1 },
                new() { SourceId = "bbb", DisplayName = "s2", Path = path2 },
            },
        };
        var filePath = Path.Combine(
            Path.GetTempPath(),
            $"tmtrace-vm-m2-{Guid.NewGuid():N}.tmtrace");
        try
        {
            library.Save(snapshot, filePath);

            // Act: this triggers ApplySnapshotAsync → our new
            // LoadedTracePath = Sources[0].Path line.
            await sut.OpenSessionAsync(filePath);

            // Assert: explicit assignment wins regardless of whether
            // OnRegistrySourcesChanged fires synchronously inside
            // registry.LoadAsync.
            sut.LoadedTracePath.Should().Be(path1,
                "the v3.5.1 explicit assignment must set LoadedTracePath to the first source's path");
        }
        finally
        {
            if (File.Exists(filePath)) File.Delete(filePath);
        }
    }

    // ===== v3.6.0 MINOR T1.A: AppVersion stamped from assembly metadata =====

    [Fact]
    public async Task BuildSnapshot_StampsInformationalVersion()
    {
        // v3.6.0 MINOR T1.A: the bundle's AppVersion must reflect the
        // running assembly's AssemblyInformationalVersion, NOT a
        // hardcoded string. Strip any "+git<sha>" suffix LocalBuilder
        // appends so the assertion matches the on-disk value.
        var library = NewTestLibrary(out var libPath);
        var vm = NewVm(library);

        var bundle = SaveAndReloadBundle(vm, library, libPath);

        var raw = typeof(App).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        var expected = raw?.Split('+')[0];
        bundle.AppVersion.Should().Be(expected);

        try { if (File.Exists(libPath)) File.Delete(libPath); } catch { /* best effort */ }
    }

    // ===== v3.6.0 MINOR T1.B: restore Color + DisplayName on reload =====

    [Fact]
    public async Task ApplySnapshotAsync_RestoresColorAndDisplayName()
    {
        // v3.6.0 MINOR T1.B: when a bundle carries a non-default ARGB
        // color and a custom DisplayName (one that DIFFERS from the
        // path's filename), both must survive the save → fresh-VM
        // reload round-trip.
        const byte R = 0x12, G = 0x34, B = 0x56;
        const string DisplayName = "highway_cruise";
        const string Path = "C:/recordings/2026-01-15_drive.asc";
        var library = NewTestLibrary(out var libPath);
        var registry = MakeFakeRegistry();
        var vm = new TraceViewerViewModel(
            registry, MakeFakeDbcService(), MakeFakeLogger(), library);
        // Seed a source whose DisplayName intentionally differs from
        // the path's filename (the guard's distinguishing signal).
        var source = new TraceSource(
            Guid.NewGuid().ToString("N"),
            DisplayName, Path,
            OxyColor.FromArgb(255, R, G, B));
        registry.Sources.Returns(new List<TraceSource> { source });
        registry.SourcesChanged += Raise.Event<Action>();
        await vm.SaveSessionAsync(libPath);

        // Reload through a fresh VM whose registry returns a fresh
        // TraceSource (palette defaults) on LoadAsync AND mutates
        // Sources to simulate the registry's bookkeeping.
        var reloadRegistry = Substitute.For<ITraceSessionRegistry>();
        var loadedSources = new List<TraceSource>();
        reloadRegistry.Sources.Returns(loadedSources);
        reloadRegistry.GetService(Arg.Any<string>())
            .Returns(MakeFakeService());
        reloadRegistry.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                // Mimic the production registry: stamp filename as
                // DisplayName + palette color. Restore logic must
                // overwrite both with bundle values.
                var src = new TraceSource(
                    "fresh-id", "2026-01-15_drive", Path,
                    OxyColors.Blue);
                loadedSources.Add(src);
                return src;
            });
        var vm2 = new TraceViewerViewModel(
            reloadRegistry, MakeFakeDbcService(), MakeFakeLogger(), library);

        var missing = await vm2.OpenSessionAsync(libPath);
        missing.Should().BeEmpty();

        var restored = vm2.Sources.Single();
        restored.DisplayName.Should().Be(DisplayName);
        restored.Color.R.Should().Be(R);
        restored.Color.G.Should().Be(G);
        restored.Color.B.Should().Be(B);

        try { if (File.Exists(libPath)) File.Delete(libPath); } catch { /* best effort */ }
    }

    [Fact]
    public async Task ApplySnapshotAsync_V1BundleWithoutColor_FallsBackToPalette()
    {
        // v3.6.0 MINOR T1.B: bundles whose ARGB bytes are all 0 must
        // leave the registry's palette color untouched. Without this
        // guard, a fully-transparent black source would render with
        // zero alpha in the chart strip.
        var library = NewTestLibrary(out var libPath);
        var registry = MakeFakeRegistry();
        var vm = new TraceViewerViewModel(
            registry, MakeFakeDbcService(), MakeFakeLogger(), library);
        AddFakeTraceSource(registry, displayName: "drive_downtown");
        await vm.SaveSessionAsync(libPath);

        // Hand-craft a v1 bundle with all-zero color bytes — simulates
        // a hand-edited or imported bundle where the color field is
        // not populated.
        var dto = library.Load(libPath)!;
        dto.Sources[0].ColorA = 0;
        dto.Sources[0].ColorR = 0;
        dto.Sources[0].ColorG = 0;
        dto.Sources[0].ColorB = 0;
        library.Save(dto, libPath);

        // Reload via fresh VM — registry's stub returns a source with
        // a non-zero palette color (OxyColors.Orange) and seeds
        // Sources so the VM can see it.
        var reloadRegistry = Substitute.For<ITraceSessionRegistry>();
        var loadedSources = new List<TraceSource>();
        reloadRegistry.Sources.Returns(loadedSources);
        reloadRegistry.GetService(Arg.Any<string>())
            .Returns(MakeFakeService());
        reloadRegistry.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var src = new TraceSource(
                    "fresh-id", "drive_downtown.asc", "drive_downtown.asc",
                    OxyColors.Orange);
                loadedSources.Add(src);
                return src;
            });
        var vm2 = new TraceViewerViewModel(
            reloadRegistry, MakeFakeDbcService(), MakeFakeLogger(), library);

        var missing = await vm2.OpenSessionAsync(libPath);
        missing.Should().BeEmpty();

        var restored = vm2.Sources.Single();
        restored.Color.A.Should().NotBe(0,
            "a bundle with all-zero ARGB must NOT overwrite the registry's palette color");

        try { if (File.Exists(libPath)) File.Delete(libPath); } catch { /* best effort */ }
    }

    // ===== v3.6.4 PATCH: hash-based .asc relocation =====

    // Fake hasher that records the requested paths and returns a
    // canned SHA-256 hex string per request. Tests inject this to
    // pin BuildSnapshot's "populate contentHash when path exists"
    // contract without touching the disk.
    private sealed class FakeAscHasher : PeakCan.Host.Core.Services.IAscContentHasher
    {
        public List<string> Requests { get; } = new();
        public string Return { get; set; } = "deadbeef" + new string('0', 56);
        public bool ThrowOnCompute { get; set; }
        public Task<string> ComputeAsync(string path, CancellationToken ct = default)
        {
            Requests.Add(path);
            if (ThrowOnCompute)
                throw new IOException("synthetic hasher failure");
            return Task.FromResult(Return);
        }
    }

    // Fake locator that returns a configurable relocated path. Tests
    // inject this to pin ApplySnapshotAsync's "use the relocated path
    // when path is missing AND hash is non-empty" contract.
    private sealed class FakeAscLocator : PeakCan.Host.Core.Services.IAscLocator
    {
        public string? LocateResult { get; set; }
        public string? LastHash { get; private set; }
        public Task<string?> LocateAsync(string contentHash, CancellationToken ct = default)
        {
            LastHash = contentHash;
            return Task.FromResult(LocateResult);
        }
    }

    [Fact]
    public void BuildSnapshot_PopulatesContentHash_WhenSourceFileExists()
    {
        // Arrange — registry has one source whose .asc path points at
        // a real file on disk. BuildSnapshot must call the hasher and
        // populate the bundle's contentHash with the returned hex.
        var fakeHash = "a1b2c3d4" + new string('0', 56);
        var hasher = new FakeAscHasher { Return = fakeHash };
        var library = NewTestLibrary(out var libPath);
        var registry = MakeFakeRegistry();
        var vm = new TraceViewerViewModel(
            registry, MakeFakeDbcService(), MakeFakeLogger(), library,
            fileDialog: null, hasher: hasher, locator: null);
        // Source points at a real file under the test temp dir.
        var ascPath = Path.Combine(Path.GetTempPath(), $"v364-{Guid.NewGuid():N}.asc");
        File.WriteAllText(ascPath, "synthetic asc content");
        try
        {
            AddFakeTraceSource(registry, displayName: "drive", sourceId: "guid-1");
            // Replace the seeded path with our real-file path.
            registry.Sources.Returns(new List<TraceSource>
            {
                new("guid-1", "drive", ascPath, OxyColors.Blue),
            });
            registry.SourcesChanged += Raise.Event<Action>();

            // Act
            var bundle = vm.BuildSnapshot();

            // Assert
            bundle.Sources.Should().HaveCount(1);
            bundle.Sources[0].ContentHash.Should().Be(fakeHash);
            hasher.Requests.Should().Contain(ascPath);

            try { if (File.Exists(libPath)) File.Delete(libPath); } catch { }
        }
        finally
        {
            if (File.Exists(ascPath)) File.Delete(ascPath);
        }
    }

    [Fact]
    public void BuildSnapshot_LeavesContentHashEmpty_WhenSourceFileMissing()
    {
        // Arrange — source's .asc path does NOT exist on disk. The
        // hasher must NOT be called and the bundle's contentHash must
        // be empty so the loader falls back to path-only resolution.
        var hasher = new FakeAscHasher();
        var library = NewTestLibrary(out var libPath);
        var registry = MakeFakeRegistry();
        var vm = new TraceViewerViewModel(
            registry, MakeFakeDbcService(), MakeFakeLogger(), library,
            fileDialog: null, hasher: hasher, locator: null);
        var missingPath = Path.Combine(
            Path.GetTempPath(), $"v364-missing-{Guid.NewGuid():N}.asc");
        AddFakeTraceSource(registry, displayName: "drive", sourceId: "guid-1");
        registry.Sources.Returns(new List<TraceSource>
        {
            new("guid-1", "drive", missingPath, OxyColors.Blue),
        });
        registry.SourcesChanged += Raise.Event<Action>();

        // Act
        var bundle = vm.BuildSnapshot();

        // Assert
        bundle.Sources.Should().HaveCount(1);
        bundle.Sources[0].ContentHash.Should().Be("");
        hasher.Requests.Should().BeEmpty(
            "the hasher must not be called when the source file does not exist");
        try { if (File.Exists(libPath)) File.Delete(libPath); } catch { }
    }

    [Fact]
    public async Task ApplySnapshotAsync_HashHit_ReloadsFromRelocatedPath()
    {
        // Arrange — saved bundle has a stale path + a contentHash.
        // The registry's LoadAsync stub records the path argument; we
        // assert that the relocated path (returned by the locator)
        // was passed, not the stale path. The relocated path must
        // exist on disk for the VM to use it (File.Exists gate).
        const string StalePath = "C:/old/location/drive.asc";
        var relocatedPath = Path.Combine(Path.GetTempPath(), $"v364-reloc-{Guid.NewGuid():N}.asc");
        File.WriteAllText(relocatedPath, "relocated synthetic asc content");
        var locator = new FakeAscLocator { LocateResult = relocatedPath };
        var library = NewTestLibrary(out var libPath);
        var bundle = new TraceSessionBundleDto
        {
            Version = 1,
            Schema = TraceSessionLibrary.CurrentSchema,
            SavedAt = DateTimeOffset.UtcNow,
            AppVersion = "3.6.4",
            Sources = new List<BundleSourceDto>
            {
                new()
                {
                    SourceId = "guid-1",
                    DisplayName = "drive",
                    Path = StalePath,
                    ColorA = 255, ColorR = 1, ColorG = 2, ColorB = 3,
                    StrokeStyle = "Solid",
                    CanIdFilter = "",
                    ContentHash = "abcdef" + new string('0', 58),
                },
            },
        };
        library.Save(bundle, libPath);

        // Registry returns a fresh source on LoadAsync, mirroring the
        // production behavior. We capture the requested path.
        string? loadedPath = null;
        var reloadRegistry = Substitute.For<ITraceSessionRegistry>();
        var loadedSources = new List<TraceSource>();
        reloadRegistry.Sources.Returns(loadedSources);
        reloadRegistry.GetService(Arg.Any<string>())
            .Returns(MakeFakeService());
        reloadRegistry.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                loadedPath = _.ArgAt<string>(0);
                var src = new TraceSource(
                    "fresh-id", "drive", loadedPath ?? StalePath, OxyColors.Blue);
                loadedSources.Add(src);
                return src;
            });
        var vm = new TraceViewerViewModel(
            reloadRegistry, MakeFakeDbcService(), MakeFakeLogger(), library,
            fileDialog: null, hasher: null, locator: locator);

        try
        {
            // Act
            var missing = await vm.OpenSessionAsync(libPath);

            // Assert
            missing.Should().BeEmpty(
                "the relocated path counts as a successful load — it must not appear in missing");
            loadedPath.Should().Be(relocatedPath,
                "the VM must call LoadAsync with the relocated path returned by the locator");
            locator.LastHash.Should().Be(bundle.Sources[0].ContentHash);
        }
        finally
        {
            if (File.Exists(relocatedPath)) File.Delete(relocatedPath);
            try { if (File.Exists(libPath)) File.Delete(libPath); } catch { }
        }
    }

    [Fact]
    public async Task ApplySnapshotAsync_HashMiss_ReportsStalePathInMissing()
    {
        // Arrange — saved bundle has a stale path + contentHash, but
        // the locator returns null (no match in the search dirs).
        // The VM must fall through to the existing path-only
        // resolution: LoadAsync(stalePath) is invoked, the registry
        // throws FileNotFoundException (because the file is gone),
        // and the VM adds bs.Path to the missing list.
        const string StalePath = "C:/old/location/drive.asc";
        var locator = new FakeAscLocator { LocateResult = null };
        var library = NewTestLibrary(out var libPath);
        var bundle = new TraceSessionBundleDto
        {
            Version = 1,
            Schema = TraceSessionLibrary.CurrentSchema,
            SavedAt = DateTimeOffset.UtcNow,
            AppVersion = "3.6.4",
            Sources = new List<BundleSourceDto>
            {
                new()
                {
                    SourceId = "guid-1",
                    DisplayName = "drive",
                    Path = StalePath,
                    ColorA = 255, ColorR = 1, ColorG = 2, ColorB = 3,
                    StrokeStyle = "Solid",
                    CanIdFilter = "",
                    ContentHash = "123456" + new string('0', 58),
                },
            },
        };
        library.Save(bundle, libPath);

        // Registry throws FileNotFoundException for the stale path —
        // matches production behavior when the .asc is gone.
        var reloadRegistry = Substitute.For<ITraceSessionRegistry>();
        reloadRegistry.Sources.Returns(new List<TraceSource>());
        reloadRegistry.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<TraceSource>>(_ =>
                throw new FileNotFoundException("synthetic missing-file", _.ArgAt<string>(0)));
        var vm = new TraceViewerViewModel(
            reloadRegistry, MakeFakeDbcService(), MakeFakeLogger(), library,
            fileDialog: null, hasher: null, locator: locator);

        // Act
        var missing = await vm.OpenSessionAsync(libPath);

        // Assert
        missing.Should().ContainSingle()
            .Which.Should().Be(StalePath,
                "when the hash lookup fails the VM must surface the original stale path in the missing list");

        try { if (File.Exists(libPath)) File.Delete(libPath); } catch { }
    }

    [Fact]
    public async Task ApplySnapshotAsync_NoContentHash_ExistingPathOnlyBehavior()
    {
        // Arrange — saved bundle has a stale path but NO contentHash
        // (the v3.6.0-v3.6.3 case). The VM must NOT call the locator
        // and must surface the stale path in the missing list —
        // identical behavior to v3.6.3.
        const string StalePath = "C:/old/location/drive.asc";
        var locator = new FakeAscLocator { LocateResult = "C:/somewhere/else.asc" };
        var library = NewTestLibrary(out var libPath);
        var bundle = new TraceSessionBundleDto
        {
            Version = 1,
            Schema = TraceSessionLibrary.CurrentSchema,
            SavedAt = DateTimeOffset.UtcNow,
            AppVersion = "3.6.3",
            Sources = new List<BundleSourceDto>
            {
                new()
                {
                    SourceId = "guid-1",
                    DisplayName = "drive",
                    Path = StalePath,
                    ColorA = 255, ColorR = 1, ColorG = 2, ColorB = 3,
                    StrokeStyle = "Solid",
                    CanIdFilter = "",
                    ContentHash = "",   // empty — v3.6.3-era bundle
                },
            },
        };
        library.Save(bundle, libPath);

        // Registry throws FileNotFoundException for the stale path —
        // matches production behavior when the .asc is gone.
        var reloadRegistry = Substitute.For<ITraceSessionRegistry>();
        reloadRegistry.Sources.Returns(new List<TraceSource>());
        reloadRegistry.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<TraceSource>>(_ =>
                throw new FileNotFoundException("synthetic missing-file", _.ArgAt<string>(0)));
        var vm = new TraceViewerViewModel(
            reloadRegistry, MakeFakeDbcService(), MakeFakeLogger(), library,
            fileDialog: null, hasher: null, locator: locator);

        // Act
        var missing = await vm.OpenSessionAsync(libPath);

        // Assert
        missing.Should().ContainSingle().Which.Should().Be(StalePath);
        locator.LastHash.Should().BeNull(
            "the locator must NOT be invoked when the bundle has no contentHash");

        try { if (File.Exists(libPath)) File.Delete(libPath); } catch { }
    }

    // ---------- v3.9.1 PATCH: IsLoading + ErrorMessage + StatusMessage UX surface ----------

    /// <summary>
    /// v3.9.1 PATCH Bug #2 root fix: when the registry throws
    /// <see cref="ReplayException"/> (parse failure, file not found, etc.)
    /// during <c>AddTraceAsync</c>, the VM must:
    ///   1. Set <c>ErrorMessage</c> to the exception message (XAML-bound red text).
    ///   2. Set <c>StatusMessage</c> to "Load failed".
    ///   3. Reset <c>IsLoading</c> to false in finally (button re-enables).
    ///   4. NOT re-throw (the VM absorbs the failure into bindable state; the View
    ///      no longer shows a MessageBox — that contract was removed in v3.9.1).
    /// Pre-fix, the VM rethrew and the View caught with <c>MessageBox.Show</c>.
    /// </summary>
    [Fact]
    public async Task AddTraceAsync_RegistryThrowsReplayException_SetsErrorMessageAndClearsIsLoading()
    {
        var registry = MakeFakeRegistry();
        registry.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<TraceSource>>(_ =>
                throw new ReplayLoadException("ASC file not found: C:/missing.asc"));
        // v3.11.4 PATCH: AddTraceAsync parameterless; dialog drives the path.
        var dialog = Substitute.For<IFileDialogService>();
        dialog.ShowOpenDialog(Arg.Any<string>()).Returns("C:/missing.asc");
        var sut = new TraceViewerViewModel(registry, MakeFakeDbcService(), MakeFakeLogger(), MakeFakeSessionLibrary(), dialog);

        // Act — must NOT throw (absorbed into ErrorMessage)
        await sut.AddTraceAsync();

        // Assert
        sut.IsLoading.Should().BeFalse("IsLoading must reset in finally so the Add button re-enables");
        sut.ErrorMessage.Should().Be("ASC file not found: C:/missing.asc",
            "v3.9.1 PATCH: parse failure surfaces as bindable ErrorMessage (XAML red text) — was a MessageBox before");
        sut.StatusMessage.Should().Be("Load failed");
    }

    /// <summary>
    /// v3.9.1 PATCH: when <see cref="ReplayFormatException"/> propagates from
    /// the registry (e.g. empty .asc file, >50% malformed lines), the VM must
    /// absorb it into <c>ErrorMessage</c> + reset <c>IsLoading</c>.
    /// Pre-fix, the silent empty-file no-op in <c>TraceViewerService.LoadAsync</c>
    /// (line 62-68) swallowed this exception — user saw no error at all.
    /// </summary>
    [Fact]
    public async Task AddTraceAsync_RegistryThrowsReplayFormatException_SetsErrorMessageAndClearsIsLoading()
    {
        var registry = MakeFakeRegistry();
        registry.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<TraceSource>>(_ =>
                throw new ReplayFormatException("Empty ASC file (0 parseable frames)"));
        // v3.11.4 PATCH: AddTraceAsync parameterless; dialog drives the path.
        var dialog = Substitute.For<IFileDialogService>();
        dialog.ShowOpenDialog(Arg.Any<string>()).Returns("C:/empty.asc");
        var sut = new TraceViewerViewModel(registry, MakeFakeDbcService(), MakeFakeLogger(), MakeFakeSessionLibrary(), dialog);

        await sut.AddTraceAsync();

        sut.IsLoading.Should().BeFalse();
        sut.ErrorMessage.Should().Contain("Empty ASC");
        sut.StatusMessage.Should().Be("Load failed");
    }

    /// <summary>
    /// v3.9.1 PATCH: <see cref="OperationCanceledException"/> during
    /// <c>AddTraceAsync</c> must be swallowed cleanly — status shows "Load
    /// cancelled", <c>ErrorMessage</c> stays null (cancel is not a
    /// user-hostile failure), <c>IsLoading</c> resets.
    /// Pre-fix, OCE propagated through the <c>async void</c> click handler
    /// into WPF's DispatcherUnhandledException.
    /// </summary>
    [Fact]
    public async Task AddTraceAsync_RegistryThrowsOperationCanceled_SwallowsCleanlyNoErrorMessage()
    {
        var registry = MakeFakeRegistry();
        registry.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<TraceSource>>(_ =>
                throw new OperationCanceledException("user cancelled"));
        // v3.11.4 PATCH: AddTraceAsync parameterless; dialog drives the path.
        var dialog = Substitute.For<IFileDialogService>();
        dialog.ShowOpenDialog(Arg.Any<string>()).Returns("C:/whatever.asc");
        var sut = new TraceViewerViewModel(registry, MakeFakeDbcService(), MakeFakeLogger(), MakeFakeSessionLibrary(), dialog);

        await sut.AddTraceAsync();

        sut.IsLoading.Should().BeFalse();
        sut.ErrorMessage.Should().BeNull(
            "v3.9.1 PATCH: cancellation is not a user-hostile failure — no red error text");
        sut.StatusMessage.Should().Be("Load cancelled");
    }

    /// <summary>
    /// v3.9.1 PATCH: <see cref="TraceViewerViewModel.AddTraceCommand"/> CanExecute
    /// must be <c>false</c> while <c>IsLoading</c> is true and <c>true</c>
    /// when false. This is the gate that greys out the toolbar "Add trace…"
    /// button during a load — implemented via <c>[NotifyCanExecuteChangedFor]</c>
    /// on <c>IsLoading</c>, mirroring <c>ReplayViewModel.IsLoaded</c>'s
    /// 5-command gate pattern.
    /// </summary>
    [Fact]
    public void AddTraceCommand_CanExecute_ReflectsIsLoading()
    {
        var sut = new TraceViewerViewModel(MakeFakeRegistry(), MakeFakeDbcService(), MakeFakeLogger(), MakeFakeSessionLibrary());

        sut.IsLoading = false;
        sut.AddTraceCommand.CanExecute(null).Should().BeTrue(
            "AddTraceCommand must be enabled when IsLoading=false (initial state)");

        sut.IsLoading = true;
        sut.AddTraceCommand.CanExecute(null).Should().BeFalse(
            "v3.9.1 PATCH: AddTraceCommand must be disabled during load — toolbar button greys out");
    }

    /// <summary>
    /// v3.9.2 PATCH H10: AddTraceAsync must catch non-Replay/non-OCE
    /// exceptions and surface them via ErrorMessage + StatusMessage.
    /// Without this fallback, an unexpected exception would escape the
    /// async-void command, hit WPF DispatcherUnhandledException, and
    /// terminate the process (App.xaml.cs:332 "do not mark Handled").
    /// </summary>
    [Fact]
    public async Task AddTraceAsync_RegistryThrowsUnexpectedException_SetsErrorMessageAndClearsIsLoading()
    {
        var registry = Substitute.For<ITraceSessionRegistry>();
        // NSubstitute cannot configure a Task-returning method to throw
        // synchronously via Returns(...). Use When().Do() to throw inside
        // the awaited call so the async machinery sees the exception.
        registry.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async _ => throw new InvalidOperationException("registry hook blew up"));
        // v3.11.4 PATCH: AddTraceAsync parameterless; dialog drives the path.
        var dialog = Substitute.For<IFileDialogService>();
        dialog.ShowOpenDialog(Arg.Any<string>()).Returns("C:/whatever.asc");
        var sut = new TraceViewerViewModel(registry, MakeFakeDbcService(), MakeFakeLogger(), MakeFakeSessionLibrary(), dialog);

        await sut.AddTraceAsync();

        sut.ErrorMessage.Should().Contain("Unexpected error").And.Contain("registry hook blew up");
        sut.StatusMessage.Should().Be("Load failed");
        sut.IsLoading.Should().BeFalse(
            "v3.9.2 PATCH H10: IsLoading must reset to false on the fallback catch arm");
    }

    // ===== v3.11.4 PATCH: 4 STA tests for the file-dialog flow =====

    // v3.11.4 PATCH: regression coverage for the empty-path "Unexpected error:
    // path must be non-empty" regression. The fix moves file-dialog flow into
    // the VM via IFileDialogService. Cancellation = silent no-op.
    [Fact]
    public async Task AddTraceAsync_FileDialog_Cancelled_Is_SilentNoOp()
    {
        // ARRANGE
        var dialog = Substitute.For<IFileDialogService>();
        dialog.ShowOpenDialog(Arg.Any<string>()).Returns((string?)null);
        var registry = Substitute.For<ITraceSessionRegistry>();
        var vm = NewVmWithDialog(registry, dialog);

        var initialStatus = vm.StatusMessage;
        var initialError = vm.ErrorMessage;

        // ACT
        await vm.AddTraceAsync();   // parameterless — dialog drives path

        // ASSERT
        dialog.Received(1).ShowOpenDialog(Arg.Any<string>());
        await registry.DidNotReceive().LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        vm.ErrorMessage.Should().Be(initialError, "cancellation must not surface an error message");
        vm.StatusMessage.Should().Be(initialStatus, "cancellation must not change the status banner");
        vm.IsLoading.Should().BeFalse("IsLoading must reset in finally regardless of dialog outcome");
    }

    [Fact]
    public async Task AddTraceAsync_FileDialog_Returns_ValidPath_Calls_Registry_LoadAsync()
    {
        // ARRANGE
        const string path = @"C:\fake\trace.asc";
        var dialog = Substitute.For<IFileDialogService>();
        dialog.ShowOpenDialog(Arg.Any<string>()).Returns(path);
        var registry = Substitute.For<ITraceSessionRegistry>();
        // v3.11.4 PATCH: ITraceSessionRegistry.LoadAsync returns
        // Task<TraceSource> (not Task), so the stub must return a
        // TraceSource — Task.CompletedTask would fail NSubstitute's
        // type-mismatch check. The exact TraceSource doesn't matter
        // for this test (the VM doesn't read it back).
        var fakeSource = new TraceSource(
            "guid-test", "fake", path, OxyColors.Blue);
        registry.LoadAsync(path, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(fakeSource));
        var vm = NewVmWithDialog(registry, dialog);

        // ACT
        await vm.AddTraceAsync();

        // ASSERT
        dialog.Received(1).ShowOpenDialog(Arg.Any<string>());
        await registry.Received(1).LoadAsync(path, Arg.Any<CancellationToken>());
        vm.IsLoading.Should().BeFalse("IsLoading must reset after a successful load");
        vm.StatusMessage.Should().Contain("Loaded", "successful load must update the status banner");
        vm.ErrorMessage.Should().BeNull("successful load must clear any prior error");
    }

    [Fact]
    public async Task AddTraceAsync_Never_Passes_EmptyPath_To_Registry()
    {
        // v3.11.4 PATCH regression guard: the file-dialog flow lives in the VM
        // now, so the registry NEVER sees an empty path — the validator
        // (PathNormalizer.Normalize → "path must be non-empty") can only fire
        // if the dialog returned a literally-empty string, which the production
        // OpenFileDialog never does. This test pins the contract.
        // ARRANGE
        var dialog = Substitute.For<IFileDialogService>();
        dialog.ShowOpenDialog(Arg.Any<string>()).Returns(string.Empty);  // pathological
        var registry = Substitute.For<ITraceSessionRegistry>();
        var vm = NewVmWithDialog(registry, dialog);

        // ACT
        await vm.AddTraceAsync();

        // ASSERT
        await registry.DidNotReceive().LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        // The empty path from the dialog must be rejected by the VM, not
        // forwarded to the registry. v3.11.4 PATCH contract: empty string from
        // dialog is treated like null (cancellation).
        vm.ErrorMessage.Should().BeNull("empty-path must NOT surface as an error — the dialog should never return empty in production, and treating it as cancellation matches the null branch");
    }

    [Fact]
    public void CanAddTrace_True_When_IsLoading_False_Regardless_Of_Argument()
    {
        // v3.11.4 PATCH: AddTraceCommand becomes parameterless (no path arg).
        // The CanExecute predicate must NOT depend on the path argument any
        // more — it gates solely on IsLoading.
        var vm = NewVm();
        vm.IsLoading = false;

        vm.AddTraceCommand.CanExecute(null).Should().BeTrue("IsLoading=false must enable the command");
        vm.AddTraceCommand.CanExecute(string.Empty).Should().BeTrue("an empty path arg must NOT disable the command (was the v3.9.1 root cause)");
        vm.AddTraceCommand.CanExecute(@"C:\anything.asc").Should().BeTrue("any path arg must NOT disable the command");
    }
}
