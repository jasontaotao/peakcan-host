using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ScottPlot;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.ViewModels;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.Replay;
using Xunit;
using FrameFlags = PeakCan.HIL.Core.FrameFlags;
using ValueType = PeakCan.HIL.Core.Dbc.ValueType;

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

    // v3.x (会话状态剥离 Task 3): ITraceSessionService 替身——配置非空集合，否则
    // VM ctor 对 WatchedSignals.CollectionChanged 的订阅会 NRE（替身默认返回 null）。
    private static ITraceSessionService MakeFakeSession()
    {
        var session = Substitute.For<ITraceSessionService>();
        session.WatchedSignals.Returns(new ObservableCollection<WatchedSignalRow>());
        session.SignalGroups.Returns(new ObservableCollection<WatchedSignalGroup>());
        return session;
    }

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
        => new TraceViewerViewModel(MakeFakeSession(),
            MakeFakeRegistry(),
            MakeFakeDbcService(),
            MakeFakeLogger(),
            library);

    // v3.11.4 PATCH: no-args overload that the CanExecute test
    // (CanAddTrace_True_When_IsLoading_False_Regardless_Of_Argument)
    // depends on. Delegates to the real TraceSessionLibrary ctor with a
    // per-test temp path (mirrors AppShellViewModelTests.NewFakeSessionLibrary).
    private static TraceViewerViewModel NewVm()
        => new TraceViewerViewModel(MakeFakeSession(),
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
        return new TraceViewerViewModel(MakeFakeSession(),registry, dbcService, logger, sessionLibrary, fileDialog: dialog);
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
        Color? color = null,
        string? sourceId = null)
    {
        var src = new TraceSource(
            sourceId ?? Guid.NewGuid().ToString("N"),
            displayName,
            $"C:/fake.asc",
            color ?? Colors.Blue,
            new LineStyle());
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

    // Timestamp defaults to 0.0 for legacy callers; tests that assert
    // "last frame wins" (RefreshFrameCounts picks MaxBy(Timestamp)) must
    // pass strictly increasing timestamps — MaxBy returns the FIRST
    // element on ties, so all-equal timestamps would decode frame 0.
    private static ReplayFrame Frame(uint id, params byte[] data) => Frame(id, 0.0, data);

    private static ReplayFrame Frame(uint id, double timestamp, params byte[] data) =>
        new(Timestamp: timestamp, Id: id, Dlc: (byte)data.Length, Data: data, Flags: FrameFlags.None);

    [Fact]
    public void Ctor_Empty_NoSignalsNoCharts()
    {
        var sut = new TraceViewerViewModel(MakeFakeSession(),MakeFakeRegistry(), MakeFakeDbcService(), MakeFakeLogger(), MakeFakeSessionLibrary());
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
        var sut = new TraceViewerViewModel(MakeFakeSession(),svc, MakeFakeDbcService(), MakeFakeLogger(), MakeFakeSessionLibrary(), fileDialog: dialog);
        await sut.AddTraceAsync();
        await svc.Received(1).LoadAsync("C:/fake.asc", Arg.Any<CancellationToken>());
    }

    // ===== v3.0.1 PATCH Task 2: per-signal DBC decode =====
    // v3.16.9.3 PATCH: these tests originally asserted sut.Signals (the
    // v3.14.3 legacy DBC 全列 collection). v3.15.0 MINOR changed the
    // contract to user opt-in via WatchedSignals — the Signals
    // collection is preserved for back-compat but no longer populated
    // (see TraceViewerViewModel.cs:131-138). Tests rewritten to drive
    // AddToWatch first, then assert WatchedSignals content + LatestValue.

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
        var sut = new TraceViewerViewModel(MakeFakeSession(),svc, dbc, MakeFakeLogger(), MakeFakeSessionLibrary(), fileDialog: dialog);

        await sut.AddTraceAsync();

        // v3.15.0 contract: Signals is intentionally empty (no DBC +
        // no AddToWatch). Asserting empty documents the v3.15.0 design
        // and guards against any future regression that auto-populates.
        sut.Signals.Should().BeEmpty();
        sut.WatchedSignals.Should().BeEmpty();
    }

    // v3.15.0 MINOR: tests below (RebuildSignalsAsync_DbcLoaded_PopulatesOneRowPerSignal,
    // RebuildSignalsAsync_MultipleSignalsSameId_PopulatesAll,
    // RebuildSignalsAsync_LatestValueIsLastDecoded) were DELETED — they
    // asserted v3.14.3 "DBC 全列" semantics which v3.15.0 explicitly
    // reverses. The new watch-list tests in
    // TraceViewerViewModelRebuildSignalsTests cover the v3.15.0
    // contracts (WatchedSignals empty by default + AddToWatch populates).

    [Fact]
    public async Task RebuildSignalsAsync_DbcLoaded_PopulatesOneRowPerSignal()
    {
        // v3.15.0 MINOR: rewritten for watch-list mode. The watch list
        // starts empty even with DBC + frames loaded; AddToWatch adds
        // exactly one row per signal-in-scope.
        var svc = MakeFakeRegistry();
        // v3.2.0 MINOR: pre-populate Sources so RebuildSignalsAsync (called
        // directly since v3.13.0 PATCH F3 removed LoadDbcAsync) has at least
        // one source to iterate.

        svc.Sources.Returns(new List<TraceSource>
        {
            new("guid-test", "fake", "C:/fake.asc", Colors.Blue, new LineStyle()),
        });
        svc.GetFrames(Arg.Any<string>()).Returns(new[]
        {
            Frame(0x100, 0.0, 0x00, 0x00),
            Frame(0x100, 1.0, 0x42, 0x01),
        });
        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        dbc.SetCurrentForTests(DocWithRpmSignal());
        var sut = new TraceViewerViewModel(MakeFakeSession(),svc, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());
        await sut.RebuildSignalsAsync();

        // v3.16.9.3 PATCH: drive AddToWatch first (v3.15.0 opt-in contract),
        // then RebuildSignalsAsync (which updates FrameCount + LatestValue).
        sut.AddToWatch(0x100, "RPM", "");
        await sut.RebuildSignalsAsync();

        sut.Signals.Should().BeEmpty("v3.15.0 contract: legacy Signals collection is no longer populated");
        // RebuildSignalsCore calls EnsurePlaceholderRow which re-adds a placeholder;
        // filter it out before asserting on the user-added row.
        var realRows = sut.WatchedSignals.Where(w => !w.IsPlaceholder).ToList();
        realRows.Should().HaveCount(1);
        var row = realRows[0];
        row.CanIdHex.Should().Be("0x100");
        row.SignalName.Should().Be("RPM");
        row.Unit.Should().Be("rpm");
        row.IsPlotted.Should().BeTrue("v3.16.x AddToWatch auto-plots the just-added row (PlotSignalFromTableRow at line 1075)");

        row.LatestValue.Should().Be(322.0);
    }

    [Fact]
    public async Task RebuildSignalsAsync_MultipleSignalsSameId_PopulatesAll()
    {
        var svc = MakeFakeRegistry();
        svc.Sources.Returns(new List<TraceSource>
        {
            new("guid-test", "fake", "C:/fake.asc", Colors.Blue, new LineStyle()),
        });
        svc.GetFrames(Arg.Any<string>()).Returns(new[]
        {
            Frame(0x100, 0x10, 0x00, 0x20, 0x00),
        });
        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        dbc.SetCurrentForTests(DocWithRpmAndTemp());
        var sut = new TraceViewerViewModel(MakeFakeSession(),svc, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());
        await sut.RebuildSignalsAsync();

        // v3.16.9.3 PATCH: AddToWatch twice (once per signal) for the same
        // CAN ID — WatchedSignals grows by 1 per call.
        sut.AddToWatch(0x100, "RPM", "");
        sut.AddToWatch(0x100, "TEMP", "");
        await sut.RebuildSignalsAsync();

        sut.Signals.Should().BeEmpty();
        var realRows = sut.WatchedSignals.Where(w => !w.IsPlaceholder).ToList();
        realRows.Should().HaveCount(2);
        realRows[0].SignalName.Should().Be("RPM");
        realRows[0].LatestValue.Should().Be(16.0);
        realRows[1].SignalName.Should().Be("TEMP");
        realRows[1].LatestValue.Should().Be(32.0);

    }

    [Fact]
    public async Task RebuildSignalsAsync_NoMatchingFrames_LeavesSignalsEmpty()
    {
        var svc = MakeFakeRegistry();
        svc.GetFrames(Arg.Any<string>()).Returns(new[]
        {
            Frame(0x555, 0x42, 0x00),
        });
        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        dbc.SetCurrentForTests(DocWithRpmSignal());
        // v3.11.4 PATCH: AddTraceAsync parameterless; dialog drives the path.
        var dialog = Substitute.For<IFileDialogService>();
        dialog.ShowOpenDialog(Arg.Any<string>()).Returns("C:/fake.asc");
        var sut = new TraceViewerViewModel(MakeFakeSession(),svc, dbc, MakeFakeLogger(), MakeFakeSessionLibrary(), fileDialog: dialog);

        await sut.AddTraceAsync();

        // v3.15.0 contract: nothing populated without an explicit AddToWatch.
        sut.Signals.Should().BeEmpty();
        sut.WatchedSignals.Should().BeEmpty();

    }

    [Fact]
    public async Task RebuildSignalsAsync_LatestValueIsLastDecoded()
    {
        var svc = MakeFakeRegistry();
        svc.Sources.Returns(new List<TraceSource>
        {
            new("guid-test", "fake", "C:/fake.asc", Colors.Blue, new LineStyle()),
        });
        svc.GetFrames(Arg.Any<string>()).Returns(new[]
        {
            Frame(0x100, 0.0, 0x01, 0x00),
            Frame(0x100, 1.0, 0xFF, 0x00),
            Frame(0x100, 2.0, 0x05, 0x00),
        });
        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        dbc.SetCurrentForTests(DocWithRpmSignal());
        var sut = new TraceViewerViewModel(MakeFakeSession(),svc, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());
        await sut.RebuildSignalsAsync();

        // v3.16.9.3 PATCH: drive AddToWatch first.
        sut.AddToWatch(0x100, "RPM", "");
        await sut.RebuildSignalsAsync();

        var realRows = sut.WatchedSignals.Where(w => !w.IsPlaceholder).ToList();
        realRows.Should().HaveCount(1);
        realRows[0].LatestValue.Should().Be(5.0,
            "LatestValue must reflect the LAST decoded frame, not the first or max");
    }

//
//     // v3.16.9 PATCH RED→GREEN: BuildOneChartSeriesForSource must add a
//     // LineAnnotation with Tag == "playback-cursor" to every series' PlotModel.
//     // The red playback cursor line is positioned by TraceChartViewModel
//     // .UpdatePlaybackCursor (TraceChartViewModel.cs:86-100) which looks up
//     // the annotation by tag. Without this annotation, UpdatePlaybackCursor
//     // is a silent no-op — the cursor never appears on screen even though
//     // PlaybackCursorX is being updated every frame.
//     //
//     // The v3.16.6 release notes flagged this diagnosis (line 42: "LineAnnotation
//     // was never created") but never fixed it. v3.16.9 PATCH is the actual fix.
//     // v3.62.0 MINOR: DELETED -- asserted on OxyPlot PlotModel.Annotations.OfType<LineAnnotation>().
// // After ScottPlot migration, playback cursor is a VerticalLine on View-owned Plot.
// // TODO: re-add as View-level test.
// // [Fact]
//     public async Task BuildOneChartSeriesForSource_CreatesPlaybackCursorLineAnnotation()
//     {
//         var svc = MakeFakeRegistry();
//         svc.Sources.Returns(new List<TraceSource>
//         {
//             new("guid-cursor-test", "fake", "C:/fake.asc", Colors.Blue, new LineStyle()),
//         });
//         svc.GetFrames(Arg.Any<string>()).Returns(new[]
//         {
//             Frame(0x100, 0x10, 0x00),
//             Frame(0x100, 0x42, 0x01),
//         });
//         var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
//         dbc.SetCurrentForTests(DocWithRpmSignal());
//         var sut = new TraceViewerViewModel(MakeFakeSession(),svc, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());
// 
//         // AddToWatch triggers BuildOneChartSeriesForSource via
//         // PlotSignalFromTableRow (line 1073). This is the v3.15.0+ user
//         // path (replaces v3.14.x's manual BuildChartSeries call).
//         sut.AddToWatch(0x100, "RPM", "");
// 
//         sut.ChartViewModel.Series.Should().HaveCount(1);
//         // v3.62.0 TODO: playback cursor is now a ScottPlot VerticalLine on the
//         // View-owned Plot. Re-add as a View-level test (requires PlotResolver mock).
//     }
// 
//     // ===== v3.16.9.2 PATCH RED: LineSeries must show discrete CAN sample
//     // points as circle markers so the user can distinguish "trend line"
//     // (interpolation) from "real CAN frame" (discrete event).
//     // Spec: docs/superpowers/specs/2026-07-09-trace-viewer-enhancements-design.md
//     // §3.6 MarkerType.Circle, MarkerSize=3.
//     // Without markers, OxyPlot's LineSeries default is MarkerType.None
//     // (a continuous line with no per-point visibility).
//     // v3.62.0 MINOR: DELETED -- asserted on OxyPlot LineSeries.MarkerType/MarkerSize.
// // After ScottPlot migration, chart is a Scatter added by View via PopulatePlot.
// // TODO: re-add as View-level test.
// // [Fact]
//     public async Task BuildOneChartSeriesForSource_LineSeries_HasMarkerTypeCircle()
//     {
//         var svc = MakeFakeRegistry();
//         svc.Sources.Returns(new List<TraceSource>
//         {
//             new("guid-marker-test", "fake", "C:/fake.asc", Colors.Blue, new LineStyle()),
//         });
//         svc.GetFrames(Arg.Any<string>()).Returns(new[]
//         {
//             Frame(0x100, 0x10, 0x00),
//             Frame(0x100, 0x42, 0x01),
//         });
//         var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
//         dbc.SetCurrentForTests(DocWithRpmSignal());
//         var sut = new TraceViewerViewModel(MakeFakeSession(),svc, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());
//         sut.AddToWatch(0x100, "RPM", "");
// 
//         sut.ChartViewModel.Series.Should().HaveCount(1);
//         // v3.62.0 TODO: LineSeries.MarkerType/MarkerSize are OxyPlot concepts.
//         // ScottPlot uses Scatter plot style. Re-add as a View-level test.
//     }
// 
//     // ===== v3.16.9.2 PATCH RED: X-axis LabelFormatter when WallClockOrigin
//     // is present. Spec §3.4 line 131-139: format as 'MM/dd HH:mm:ss' using
//     // (origin + TimeSpan.FromSeconds(x)) and CultureInfo.InvariantCulture.
//     // v3.62.0 MINOR: DELETED -- asserted on OxyPlot LinearAxis.LabelFormatter.
// // After ScottPlot migration, LabelFormatter is set on TickGenerator in PopulatePlot.
// // TODO: re-add as View-level test.
// // [Fact]
//     public async Task BuildOneChartSeriesForSource_XAxis_WithWallClockOrigin_FormatsAsMmDdHhMmSs()
//     {
//         var origin = new DateTime(2026, 7, 1, 8, 32, 1, DateTimeKind.Local);
//         var svc = MakeFakeRegistry();
//         var source = new TraceSource("guid-wallclock-test", "fake", "C:/fake.asc", Colors.Blue)
//         {
//             WallClockOrigin = origin,
//         };
//         svc.Sources.Returns(new List<TraceSource> { source });
//         svc.GetFrames(Arg.Any<string>()).Returns(new[]
//         {
//             Frame(0x100, 0x10, 0x00),
//             Frame(0x100, 0x42, 0x01),
//         });
//         var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
//         dbc.SetCurrentForTests(DocWithRpmSignal());
//         var sut = new TraceViewerViewModel(MakeFakeSession(),svc, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());
//         sut.AddToWatch(0x100, "RPM", "");
// 
//         sut.ChartViewModel.Series.Should().HaveCount(1);
//         // v3.62.0 TODO: X-axis LabelFormatter is now a ScottPlot DateTimeTicks +
//         // custom tick renderer on the View-owned Plot. Re-add as View-level test.
//     }
// 
//     // ===== v3.16.9.2 PATCH RED: X-axis LabelFormatter when WallClockOrigin
//     // is null. Spec §3.4 line 136-138: 3-tier elapsed fallback (>=1d, >=1h, <1h).
//     [Theory]
//     [InlineData(90061.0, "1.0d 01:01:01")] // >= 1d: "{x/86400:F1}d {hh:mm:ss}" (F1 → 1 decimal place)
//     [InlineData(86400.0, "1.0d 00:00:00")] // exact 1d boundary
//     [InlineData(3725.0,  "01:02:05")]       // >= 1h: "hh:mm:ss"
//     [InlineData(3600.0,  "01:00:00")]       // exact 1h boundary
//     [InlineData(3599.99, "59:59.9")]        // just under 1h boundary
//     [InlineData(125.5,   "02:05.5")]        // < 1h:  "mm:ss.f"
//     public async Task BuildOneChartSeriesForSource_XAxis_WithoutWallClockOrigin_FallsBackToElapsed(double x, string expected)
//     {
//         var svc = MakeFakeRegistry();
//         // Note: WallClockOrigin defaults to null (verified in TraceSourceTests.WallClockOrigin_DefaultsToNull)
//         svc.Sources.Returns(new List<TraceSource>
//         {
//             new("guid-elapsed-test", "fake", "C:/fake.asc", Colors.Blue, new LineStyle()),
//         });
//         svc.GetFrames(Arg.Any<string>()).Returns(new[]
//         {
//             Frame(0x100, 0x10, 0x00),
//             Frame(0x100, 0x42, 0x01),
//         });
//         var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
//         dbc.SetCurrentForTests(DocWithRpmSignal());
//         var sut = new TraceViewerViewModel(MakeFakeSession(),svc, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());
//         sut.AddToWatch(0x100, "RPM", "");
// 
//         sut.ChartViewModel.Series.Should().HaveCount(1);
//         // v3.62.0 TODO: X-axis LabelFormatter is now a ScottPlot custom tick
//         // renderer on the View-owned Plot. Re-add as View-level test.
//     }

    // ===== v3.3.0 MINOR Task 3: SetMaster command + auto-promote =====

    [Fact]
    public void SetMaster_ChangesMasterSourceId()
    {
        var registry = MakeFakeRegistry();
        var svcA = MakeFakeService();
        var svcB = MakeFakeService();
        registry.Sources.Returns(new List<TraceSource>
        {
            new("a", "A", "C:/a.asc", Colors.Blue, new LineStyle()),
            new("b", "B", "C:/b.asc", Colors.Orange, new LineStyle()),
        });
        registry.GetService("a").Returns(svcA);
        registry.GetService("b").Returns(svcB);

        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        var sut = new TraceViewerViewModel(MakeFakeSession(),registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());
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
            new("a", "A", "C:/a.asc", Colors.Blue, new LineStyle()),
        });
        registry.GetService("a").Returns(svcA);

        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        var sut = new TraceViewerViewModel(MakeFakeSession(),registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());
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
            new("a", "A", "C:/a.asc", Colors.Blue, new LineStyle()),
            new("b", "B", "C:/b.asc", Colors.Orange, new LineStyle()),
        });
        registry.GetService("a").Returns(svcA);
        registry.GetService("b").Returns(svcB);

        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        var sut = new TraceViewerViewModel(MakeFakeSession(),registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());
        sut.MasterSourceId.Should().Be("a");

        // Simulate user removing source "a" (the master)
        registry.Sources.Returns(new List<TraceSource>
        {
            new("b", "B", "C:/b.asc", Colors.Orange, new LineStyle()),
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
            new("a", "A", "C:/a.asc", Colors.Blue, new LineStyle()),
        });
        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        var sut = new TraceViewerViewModel(MakeFakeSession(),registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());

        sut.HasSources.Should().BeTrue();
    }

    [Fact]
    public void HasSources_True_WhenMultipleSources()
    {
        var registry = MakeFakeRegistry();
        registry.Sources.Returns(new List<TraceSource>
        {
            new("a", "A", "C:/a.asc", Colors.Blue, new LineStyle()),
            new("b", "B", "C:/b.asc", Colors.Orange, new LineStyle()),
        });
        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        var sut = new TraceViewerViewModel(MakeFakeSession(),registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());

        sut.HasSources.Should().BeTrue();
    }

    [Fact]
    public void HasSources_False_WhenNoSources()
    {
        var registry = MakeFakeRegistry();
        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        var sut = new TraceViewerViewModel(MakeFakeSession(),registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());

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
            new("a", "A", "C:/a.asc", Colors.Blue, new LineStyle()),
            new("b", "B", "C:/b.asc", Colors.Orange, new LineStyle()),
        });
        registry.GetService("a").Returns(svcA);
        registry.GetService("b").Returns(svcB);

        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        var sut = new TraceViewerViewModel(MakeFakeSession(),registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());

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

    // v3.x (会话状态剥离 Task 3): OpenSessionAsync 的 VM 状态恢复用例删除——
    // 打开会话逻辑已迁至 ITraceSessionService（unload/load、missing 收集、
    // DisplayName/Color 重盖印、watch/groups 恢复均在 service，由
    // TraceSessionServiceTests 覆盖）。
    // v3.x Task 4: VM 的 OpenSessionAsync 薄转发已删除（TraceSessionAutoSaver
    // 改直连 service），原转发用例随之下线——会话恢复入口只在 service 层。

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

    [Fact]
    public void BuildSnapshot_WritesDefaultPlaybackEnvelope_AndNoViewports()
    {
        // 播放已废除：Trace 的 BundlePlaybackDto 信封只写默认值，
        // viewports（chart 缩放/平移，窗口级状态）不再持久化（I-2 决策）。
        var library = NewTestLibrary(out var libPath);
        var vm = NewVm(library);

        var dto = vm.BuildSnapshot();

        dto.Playback.Should().NotBeNull();
        dto.Playback!.MasterSourceId.Should().BeNullOrEmpty();
        dto.Playback.Loop.Should().BeFalse();
        dto.Playback.Speed.Should().Be(1.0);
        dto.Playback.ScrubberValue.Should().Be(0.0);
        dto.Viewports.Should().BeEmpty();

        try { if (File.Exists(libPath)) File.Delete(libPath); } catch { /* best effort */ }
    }

    // v3.x (会话状态剥离 Task 3): ApplySnapshotAsync 已删除（功能迁至
    // ITraceSessionService.OpenSessionAsync）。原 ApplySnapshotAsync_RestoresColorAndDisplayName /
    // _V1BundleWithoutColor_FallsBackToPalette 用例由 TraceSessionServiceTests 覆盖
    // （OpenSessionAsync_CollectsMissingPaths_AndReStampsLoadedSource 等）。

    // ===== v3.6.4 PATCH: hash-based .asc relocation =====

    // Fake hasher that records the requested paths and returns a
    // canned SHA-256 hex string per request. Tests inject this to
    // pin BuildSnapshot's "populate contentHash when path exists"
    // contract without touching the disk.
    private sealed class FakeAscHasher : PeakCan.HIL.Core.Services.IAscContentHasher
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
        var vm = new TraceViewerViewModel(MakeFakeSession(),
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
                new("guid-1", "drive", ascPath, Colors.Blue, new LineStyle()),
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
        var vm = new TraceViewerViewModel(MakeFakeSession(),
            registry, MakeFakeDbcService(), MakeFakeLogger(), library,
            fileDialog: null, hasher: hasher, locator: null);
        var missingPath = Path.Combine(
            Path.GetTempPath(), $"v364-missing-{Guid.NewGuid():N}.asc");
        AddFakeTraceSource(registry, displayName: "drive", sourceId: "guid-1");
        registry.Sources.Returns(new List<TraceSource>
        {
            new("guid-1", "drive", missingPath, Colors.Blue, new LineStyle()),
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

    // v3.x (会话状态剥离 Task 3): 原 ApplySnapshotAsync_HashHit_ReloadsFromRelocatedPath /
    // _HashMiss_ReportsStalePathInMissing / _NoContentHash_ExistingPathOnlyBehavior 删除——
    // locator 按哈希重定位逻辑已迁至 ITraceSessionService.OpenSessionAsync，由
    // TraceSessionServiceTests.OpenSessionAsync_LocatorRelocatesMissingAsc_ByContentHash 覆盖。
    // (FakeAscLocator 随之删除)

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
        var sut = new TraceViewerViewModel(MakeFakeSession(),registry, MakeFakeDbcService(), MakeFakeLogger(), MakeFakeSessionLibrary(), fileDialog: dialog);

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
        var sut = new TraceViewerViewModel(MakeFakeSession(),registry, MakeFakeDbcService(), MakeFakeLogger(), MakeFakeSessionLibrary(), fileDialog: dialog);

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
        var sut = new TraceViewerViewModel(MakeFakeSession(),registry, MakeFakeDbcService(), MakeFakeLogger(), MakeFakeSessionLibrary(), fileDialog: dialog);

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
        var sut = new TraceViewerViewModel(MakeFakeSession(),MakeFakeRegistry(), MakeFakeDbcService(), MakeFakeLogger(), MakeFakeSessionLibrary());

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
        var sut = new TraceViewerViewModel(MakeFakeSession(),registry, MakeFakeDbcService(), MakeFakeLogger(), MakeFakeSessionLibrary(), fileDialog: dialog);

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
            "guid-test", "fake", path, Colors.Blue, new LineStyle());
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

    /// <summary>
    /// v3.18.0 PATCH: every freshly-constructed TraceSource must have
    /// a null WallClockOrigin (no header parsed yet). The field is
    /// populated later by TraceViewerService.LoadAsync when the ASC
    /// parser hands back a non-null origin.
    /// </summary>
    [Fact]
    public void TraceSource_NewInstance_WallClockOriginIsNull()
    {
        var src = new TraceSource("a", "A", "C:/a.asc", Colors.Blue, new LineStyle());
        src.WallClockOrigin.Should().BeNull(
            "the field defaults to null and is set later by the loader after ASC header parse");
    }

    // v3.x (会话状态剥离 Task 3): Reset() 已删除——VM 改 transient 后窗口关闭即
    // 释放实例，无需手工清理窗口级状态。原 Reset_Clears_WatchedSignals_Collection /
    // Reset_Resets_Anchor_To_NaN / Reset_Clears_SamplingRows 用例随之删除。
}

