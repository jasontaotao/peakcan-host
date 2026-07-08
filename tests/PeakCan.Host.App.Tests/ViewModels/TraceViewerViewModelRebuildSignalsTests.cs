using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OxyPlot;
using OxyPlot.Series;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core.Dbc;
using PeakCan.Host.Core.Replay;
using Xunit;
using FrameFlags = PeakCan.Host.Core.FrameFlags;
using ValueType = PeakCan.Host.Core.Dbc.ValueType;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// v3.11.0 MINOR T4 (H8): end-to-end behavior coverage for the three
/// private sub-methods extracted from
/// <see cref="TraceViewerViewModel.RebuildSignalsCore"/>.
/// The tests drive the public surface (<c>RebuildSignalsAsync</c> after
/// setting <c>CanIdFilter</c>) and assert on the bindable <c>Signals</c> +
/// <c>ChartViewModel.Series</c> outputs. v3.13.0 PATCH F3 changed the
/// public surface: <c>LoadDbcAsync</c> was deleted (toolbar button had
/// no UI feedback); tests now call <c>RebuildSignalsAsync</c> directly
/// against a DBC pre-loaded via <see cref="DbcService.SetCurrentForTests"/>.
/// <c>CanIdFilter</c>) and assert on the bindable <c>Signals</c> +
/// <c>ChartViewModel.Series</c> outputs. They pin:
/// <list type="bullet">
///   <item><b>BucketFramesByCanId</b>: global filter excludes
///     non-matching IDs (test 1); per-source filter overrides the
///     global filter (test 2).</item>
///   <item><b>BuildSignalRows</b>: one message + one signal → one
///     row (test 3); message with no matching frames is skipped
///     (test 4).</item>
///   <item><b>BuildChartSeries</b>: one source + one signal → one
///     series (test 5).</item>
/// </list>
/// </summary>
public class TraceViewerViewModelRebuildSignalsTests
{
    private static ITraceSessionRegistry MakeFakeRegistry()
    {
        var registry = Substitute.For<ITraceSessionRegistry>();
        registry.Sources.Returns(new List<TraceSource>());
        return registry;
    }

    private static ITraceViewerService MakeFakeService()
    {
        var svc = Substitute.For<ITraceViewerService>();
        svc.TotalDuration.Returns(60.0);
        return svc;
    }

    private static ILogger<TraceViewerViewModel> MakeFakeLogger()
        => Substitute.For<ILogger<TraceViewerViewModel>>();

    private static TraceSessionLibrary MakeFakeSessionLibrary()
        => new TraceSessionLibrary(
            Path.Combine(Path.GetTempPath(), $"tmtrace-vm-h8-{Guid.NewGuid():N}.tmtrace"),
            NullLogger<TraceSessionLibrary>.Instance);

    private static DbcDocument DocWithTwoMessages() => new(
        Version: "",
        Nodes: System.Array.Empty<Node>(),
        Messages: new[]
        {
            new Message(Id: 0x100, Name: "M_RPM", Dlc: 8, Sender: "ECU",
                Signals: new[]
                {
                    new Signal(Name: "RPM", StartBit: 0, Length: 16,
                               Order: ByteOrder.LittleEndian,
                               ValueType: ValueType.Unsigned,
                               Factor: 1.0, Offset: 0.0,
                               Min: 0, Max: 1000, Unit: "rpm",
                               Receivers: System.Array.Empty<string>()),
                },
                IsMultiplexed: false, MultiplexorSignalIndex: null),
            new Message(Id: 0x200, Name: "M_TEMP", Dlc: 8, Sender: "ECU",
                Signals: new[]
                {
                    new Signal(Name: "Temp", StartBit: 0, Length: 16,
                               Order: ByteOrder.LittleEndian,
                               ValueType: ValueType.Unsigned,
                               Factor: 1.0, Offset: 0.0,
                               Min: -50, Max: 200, Unit: "C",
                               Receivers: System.Array.Empty<string>()),
                },
                IsMultiplexed: false, MultiplexorSignalIndex: null),
        },
        MessagesById: new Dictionary<uint, Message>(),
        ValueTables: new Dictionary<string, ValueTable>());

    private static ReplayFrame Frame(uint id, params byte[] data) =>
        new(Timestamp: 0.0, Id: id, Dlc: (byte)data.Length,
            Data: data, Flags: FrameFlags.None);

    /// <summary>
    /// v3.11.0 T4 (H8): when the global <c>CanIdFilter</c> is set to
    /// a single ID, the bucket loop in <c>BucketFramesByCanId</c>
    /// must exclude any frame whose ID is not in the allow-set. The
    /// signal-list and chart outputs must therefore only contain
    /// rows/series for the matching ID.
    /// </summary>
    [Fact]
    public async Task BucketFramesByCanId_WithGlobalFilter_ExcludesNonMatchingIds()
    {
        // Arrange: one source emitting both 0x100 and 0x200 frames.
        var registry = MakeFakeRegistry();
        var svc = MakeFakeService();
        registry.Sources.Returns(new List<TraceSource>
        {
            new("a", "traceA", "C:/a.asc", OxyColors.Blue, LineStyle.Solid),
        });
        registry.GetService("a").Returns(svc);
        registry.GetFrames("a").Returns(new[]
        {
            Frame(0x100, 0x10, 0x00),
            Frame(0x200, 0x20, 0x00),
        });

        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        dbc.SetCurrentForTests(DocWithTwoMessages());
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());
        // v3.13.0 PATCH F3: LoadDbcAsync was deleted — tests now drive
        // RebuildSignalsAsync directly. The DBC is pre-loaded via
        // DbcService.SetCurrentForTests (mirrors DbcView's runtime path).
        await sut.RebuildSignalsAsync();

        // Baseline (no filter): both messages contribute → 2 signal rows.
        sut.Signals.Should().HaveCount(2);

        // Act: set global filter to 0x100 only.
        sut.CanIdFilter = "0x100";

        // Assert: 0x200 frames are excluded by BucketFramesByCanId, so
        // only 0x100's row + series survive.
        sut.Signals.Should().ContainSingle()
            .Which.CanIdHex.Should().Be("0x100");
        sut.ChartViewModel.Series.Should().ContainSingle()
            .Which.SourceId.Should().Be("a");
    }

    /// <summary>
    /// v3.11.0 T4 (H8): the per-source filter overrides the global
    /// filter inside the bucket loop. With two sources and a global
    /// filter of "0x100", setting source A's per-source filter to
    /// "0x200" must flip source A's effective bucket to 0x200 while
    /// source B (empty per-source) inherits the global 0x100.
    /// </summary>
    [Fact]
    public async Task BucketFramesByCanId_WithPerSourceFilter_OverridesGlobalFilter()
    {
        // Arrange: two sources, each emitting 0x100 + 0x200.
        var registry = MakeFakeRegistry();
        var svcA = MakeFakeService();
        var svcB = MakeFakeService();
        var srcA = new TraceSource("a", "traceA", "C:/a.asc", OxyColors.Blue, LineStyle.Solid);
        var srcB = new TraceSource("b", "traceB", "C:/b.asc", OxyColors.Orange, LineStyle.Dash);
        registry.Sources.Returns(new List<TraceSource> { srcA, srcB });
        registry.GetService("a").Returns(svcA);
        registry.GetService("b").Returns(svcB);
        registry.GetFrames("a").Returns(new[]
        {
            Frame(0x100, 0x10, 0x00),
            Frame(0x200, 0x20, 0x00),
        });
        registry.GetFrames("b").Returns(new[]
        {
            Frame(0x100, 0x30, 0x00),
            Frame(0x200, 0x40, 0x00),
        });

        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        dbc.SetCurrentForTests(DocWithTwoMessages());
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());
        // v3.13.0 PATCH F3: LoadDbcAsync was deleted — tests now drive
        // RebuildSignalsAsync directly. The DBC is pre-loaded via
        // DbcService.SetCurrentForTests (mirrors DbcView's runtime path).
        await sut.RebuildSignalsAsync();

        // Establish global filter to 0x100; both sources narrow to 0x100.
        sut.CanIdFilter = "0x100";
        sut.Signals.Should().HaveCount(1);
        sut.ChartViewModel.Series.Should().HaveCount(2);

        // Act: set source A's per-source filter to "0x200" → source A
        // flips to 0x200; source B still inherits global 0x100.
        srcA.CanIdFilter = "0x200";

        // Assert: both 0x100 and 0x200 appear in the global bucket
        // (B contributes 0x100, A contributes 0x200) → 2 signal rows.
        sut.Signals.Should().HaveCount(2);
        // One chart series per source: source A's series is the 0x200
        // (Temp, Unit "C"), source B's series is the 0x100 (RPM, Unit "rpm").
        sut.ChartViewModel.Series.Should().HaveCount(2);
        var seriesA = sut.ChartViewModel.Series.Single(s => s.SourceId == "a");
        var seriesB = sut.ChartViewModel.Series.Single(s => s.SourceId == "b");
        seriesA.DisplayName.Should().Contain("Temp");
        seriesB.DisplayName.Should().Contain("RPM");
    }

    /// <summary>
    /// v3.11.0 T4 (H8): when the bucket contains at least one frame
    /// for a message's CAN ID, <c>BuildSignalRows</c> must emit
    /// exactly one row per signal in that message (and skip messages
    /// with no matching frames). The "LatestValue" must reflect the
    /// last decoded frame for that signal.
    /// </summary>
    [Fact]
    public async Task BuildSignalRows_OneMessageOneSignal_ProducesOneRow()
    {
        // Arrange: one source emitting two 0x100 frames. DBC has one
        // signal (RPM). Expect exactly one signal row.
        var registry = MakeFakeRegistry();
        var svc = MakeFakeService();
        registry.Sources.Returns(new List<TraceSource>
        {
            new("a", "traceA", "C:/a.asc", OxyColors.Blue, LineStyle.Solid),
        });
        registry.GetService("a").Returns(svc);
        registry.GetFrames("a").Returns(new[]
        {
            Frame(0x100, 0x00, 0x00),   // RPM = 0
            Frame(0x100, 0x42, 0x01),   // RPM = 0x0142 = 322
        });

        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        dbc.SetCurrentForTests(DocWithTwoMessages());
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());
        // v3.13.0 PATCH F3: LoadDbcAsync was deleted — tests now drive
        // RebuildSignalsAsync directly. The DBC is pre-loaded via
        // DbcService.SetCurrentForTests (mirrors DbcView's runtime path).
        await sut.RebuildSignalsAsync();

        // Assert: 0x200 has no matching frames, so BuildSignalRows skips it.
        // Only the 0x100/RPM row survives, with LatestValue = last decoded.
        sut.Signals.Should().ContainSingle();
        var row = sut.Signals[0];
        row.CanIdHex.Should().Be("0x100");
        row.SignalName.Should().Be("RPM");
        row.Unit.Should().Be("rpm");
        row.IsPlotted.Should().BeFalse();
        row.LatestValue.Should().Be(322.0,
            "LatestValue must be the LAST decoded value, not the first or max");
    }

    /// <summary>
    /// v3.11.0 T4 (H8): when the bucket contains no frames for a
    /// message's CAN ID, <c>BuildSignalRows</c> must skip that
    /// message entirely (no rows produced for its signals).
    /// </summary>
    [Fact]
    public async Task BuildSignalRows_NoMatchingFrames_SkipsMessage()
    {
        // Arrange: one source emitting only 0x555 frames. DBC defines
        // 0x100 and 0x200. Neither message has matching frames → both
        // are skipped → Signals remains empty.
        var registry = MakeFakeRegistry();
        var svc = MakeFakeService();
        registry.Sources.Returns(new List<TraceSource>
        {
            new("a", "traceA", "C:/a.asc", OxyColors.Blue, LineStyle.Solid),
        });
        registry.GetService("a").Returns(svc);
        registry.GetFrames("a").Returns(new[]
        {
            Frame(0x555, 0xAA, 0xAA),
        });

        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        dbc.SetCurrentForTests(DocWithTwoMessages());
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());
        // v3.13.0 PATCH F3: LoadDbcAsync was deleted — tests now drive
        // RebuildSignalsAsync directly. The DBC is pre-loaded via
        // DbcService.SetCurrentForTests (mirrors DbcView's runtime path).
        await sut.RebuildSignalsAsync();

        // Assert: BuildSignalRows skips both messages → Signals is empty.
        sut.Signals.Should().BeEmpty(
            "messages whose CAN IDs have no matching frames must be skipped");
    }

    /// <summary>
    /// v3.11.0 T4 (H8): <c>BuildChartSeries</c> must emit exactly
    /// one <c>TraceChartSeries</c> per (source, message, signal) triple
    /// whose bucket is non-empty. With one source + one message + one
    /// signal, expect exactly one series with the source's color and
    /// the signal's unit metadata.
    /// </summary>
    [Fact]
    public async Task BuildChartSeries_OneSourceOneSignal_AddsOneSeries()
    {
        // Arrange: one source emitting 0x100 frames. DBC has one
        // signal on 0x100 (RPM). Expect exactly one chart series.
        var registry = MakeFakeRegistry();
        var svc = MakeFakeService();
        registry.Sources.Returns(new List<TraceSource>
        {
            new("a", "traceA", "C:/a.asc", OxyColors.Blue, LineStyle.Solid),
        });
        registry.GetService("a").Returns(svc);
        registry.GetFrames("a").Returns(new[]
        {
            Frame(0x100, 0x10, 0x00),
            Frame(0x100, 0x20, 0x00),
        });

        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        dbc.SetCurrentForTests(DocWithTwoMessages());
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());
        // v3.13.0 PATCH F3: LoadDbcAsync was deleted — tests now drive
        // RebuildSignalsAsync directly. The DBC is pre-loaded via
        // DbcService.SetCurrentForTests (mirrors DbcView's runtime path).
        await sut.RebuildSignalsAsync();

        // v3.14.2 PATCH: per-signal PlotModel + decoded values are
        // now built lazily on user opt-in (PlotSignal). The series row
        // is registered at load time with a placeholder PlotModel +
        // empty XValues/YValues + IsPlotPending=true. Opt in here to
        // assert the decoded values materialize correctly.
        sut.ChartViewModel.Series.Should().ContainSingle();
        var series = sut.ChartViewModel.Series[0];
        series.SourceId.Should().Be("a");
        series.Unit.Should().Be("rpm");
        series.Color.Should().Be(OxyColors.Blue);
        series.DisplayName.Should().Contain("RPM");
        series.IsPlotPending.Should().BeTrue();
        series.YValues.Should().BeEmpty();
        // Opt the signal in: the PlotModel + YValues populate.
        sut.PlotSignal(series);
        var plotted = sut.ChartViewModel.Series[0];
        plotted.IsPlotPending.Should().BeFalse();
        plotted.YValues.Should().Equal(16.0, 32.0);
    }

    /// <summary>
    /// v3.13.2 PATCH F5: when a DBC is loaded via <see cref="DbcService.DbcLoaded"/>
    /// (the only path remaining after v3.13.0 F3 — the DbcView tab is the
    /// sole entry point), the Trace Viewer must rebuild its Signals + chart
    /// subplots. The xmldoc on the TVM (line 388) historically documented
    /// a <c>_dbcService.PropertyChanged</c> subscription, but DbcService
    /// does not implement INotifyPropertyChanged — it exposes a typed
    /// <c>DbcLoaded</c> event. The ctor now subscribes via <c>+=</c>;
    /// this test pins the contract so the subscription cannot silently
    /// regress back to a dead INPC stub.
    /// </summary>
    [Fact]
    public void DbcService_DbcLoaded_Fires_RebuildSignalsCore()
    {
        // Arrange: one source emitting 0x100 frames. No DBC loaded yet →
        // RebuildSignalsCore at ctor time sees zero decoded rows. The
        // DBC is then installed via SetCurrentForTests (does NOT raise
        // the event — see DbcService.cs:101); the test manually raises
        // DbcLoaded to drive the OnDbcLoaded handler.
        var registry = MakeFakeRegistry();
        var svc = MakeFakeService();
        registry.Sources.Returns(new List<TraceSource>
        {
            new("a", "traceA", "C:/a.asc", OxyColors.Blue, LineStyle.Solid),
        });
        registry.GetService("a").Returns(svc);
        registry.GetFrames("a").Returns(new[]
        {
            Frame(0x100, 0x10, 0x00),
            Frame(0x100, 0x20, 0x00),
        });

        var dbc = new DbcService(NullLogger<DbcService>.Instance);
        // Construct TVM BEFORE the DBC exists — mirrors the user flow
        // "open Trace Viewer → load DBC via DbcView tab → Trace Viewer
        // auto-rebuilds". At ctor time Signals is empty (no DBC).
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());
        sut.Signals.Should().BeEmpty("no DBC loaded yet at TVM construction time");

        // Act: install the DBC + raise DbcLoaded. The TVM's OnDbcLoaded
        // handler must call RebuildSignalsCore, which decodes frames
        // against the now-available DBC and populates Signals + chart.
        dbc.SetCurrentForTests(DocWithTwoMessages());
        dbc.GetType().GetEvent(nameof(DbcService.DbcLoaded))!
            .RaiseMethod(dbc, DocWithTwoMessages());

        // Assert: Signals populated — at least the 0x100 row (0x200 has
        // no matching frames in this test, so it is skipped by
        // BuildSignalRows, mirroring test 4 above). The contract under
        // test is "rebuild fired after DbcLoaded", not a specific row
        // count, so we assert "non-zero" rather than an exact figure.
        sut.Signals.Should().NotBeEmpty(
            "DbcLoaded must trigger RebuildSignalsCore so the Trace Viewer populates");
        sut.Signals[0].CanIdHex.Should().Be("0x100");
        sut.Signals[0].SignalName.Should().Be("RPM");
    }
}