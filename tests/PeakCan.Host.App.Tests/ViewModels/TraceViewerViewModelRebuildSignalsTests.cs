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
    /// v3.11.0 T4 (H8) + v3.14.3 PATCH: when the global <c>CanIdFilter</c>
    /// is set to a single ID, <c>BucketFramesByCanId</c> excludes
    /// non-matching frames. v3.14.3 PATCH: the SIGNAL TABLE is
    /// DBC-driven and shows ALL signals regardless of frame presence;
    /// only the <c>FrameCount</c> column reflects the filter. Chart
    /// rows are NOT auto-built — user opt-in creates them.
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
        await sut.RebuildSignalsAsync();

        // v3.14.3 PATCH: all DBC signals appear regardless of frames.
        sut.Signals.Should().HaveCount(2,
            "v3.14.3 PATCH: signal table is DBC-driven and shows ALL signals, not just those with matching frames");
        sut.Signals[0].CanIdHex.Should().Be("0x100");
        sut.Signals[0].FrameCount.Should().Be(1, "0x100 source has 1 frame in the bucket");
        sut.Signals[1].CanIdHex.Should().Be("0x200");
        sut.Signals[1].FrameCount.Should().Be(1, "0x200 source has 1 frame in the bucket");

        // v3.14.3 PATCH: chart series is NOT auto-built. Empty by default.
        sut.ChartViewModel.Series.Should().BeEmpty(
            "v3.14.3 PATCH: chart rows are user-opt-in via TogglePlot, not auto-built at load time");

        // Act: set global filter to 0x100 only.
        sut.CanIdFilter = "0x100";

        // Assert: FrameCount for 0x200 drops to 0 (filter excludes frames).
        // LatestValue for 0x200 becomes NaN (no frames to decode).
        var row100 = sut.Signals.Single(r => r.CanIdHex == "0x100");
        var row200 = sut.Signals.Single(r => r.CanIdHex == "0x200");
        row100.FrameCount.Should().Be(1, "0x100 frame survives the 0x100-only filter");
        row200.FrameCount.Should().Be(0, "0x200 frame is excluded by the 0x100-only filter");
        row200.LatestValue.Should().Be(double.NaN,
            "LatestValue becomes NaN when no frames survive the filter");
    }

    /// <summary>
    /// v3.11.0 T4 (H8) + v3.14.3 PATCH: the per-source filter overrides
    /// the global filter inside the bucket loop. v3.14.3 PATCH: chart
    /// rows are user opt-in; toggle after filter change to verify the
    /// per-source override is honored at opt-in time.
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
        await sut.RebuildSignalsAsync();

        // Both signals present with their frame counts (no filter).
        sut.Signals.Should().HaveCount(2);
        sut.Signals.Single(r => r.CanIdHex == "0x100").FrameCount.Should().Be(2);
        sut.Signals.Single(r => r.CanIdHex == "0x200").FrameCount.Should().Be(2);

        // Act: set global filter to 0x100 + per-source A to 0x200.
        sut.CanIdFilter = "0x100";
        srcA.CanIdFilter = "0x200";

        // Assert: per-source A's 0x100 frame is excluded (per-source filter overrides),
        // per-source B's 0x100 frame survives (inherits global).
        var row100 = sut.Signals.Single(r => r.CanIdHex == "0x100");
        var row200 = sut.Signals.Single(r => r.CanIdHex == "0x200");
        row100.FrameCount.Should().Be(1, "only source B contributes to 0x100 after per-source A override");
        row200.FrameCount.Should().Be(1, "only source A contributes to 0x200 after per-source A override");

        // v3.14.3 PATCH: opt in 0x200/Temp. Source A contributes (per-source
        // filter "0x200" allows 0x200 frames); source B inherits the
        // global "0x100" filter which excludes 0x200 → only source A
        // gets a chart series. This mirrors the pre-v3.14.3 chart
        // per-source filter resolution.
        var row200_2 = sut.Signals.Single(r => r.CanIdHex == "0x200");
        sut.SetPlotOptIn(row200_2, true);
        sut.ChartViewModel.Series.Should().ContainSingle()
            .Which.SourceId.Should().Be("a",
                "v3.14.3 PATCH: only source A contributes 0x200 frames (source B inherits global 0x100 filter)");
    }

    /// <summary>
    /// v3.11.0 T4 (H8) + v3.14.3 PATCH: when the bucket contains at
    /// least one frame for a message's CAN ID,
    /// <c>BuildSignalRowsFromDbcOnly</c> emits one row per signal with
    /// <c>FrameCount</c> + <c>LatestValue</c> populated. v3.14.3 PATCH:
    /// rows for signals with NO matching frames are also emitted, with
    /// <c>FrameCount=0</c> and <c>LatestValue=NaN</c>.
    /// </summary>
    [Fact]
    public async Task BuildSignalRows_OneMessageOneSignal_ProducesOneRow()
    {
        // Arrange: one source emitting two 0x100 frames. DBC has two
        // messages (0x100/RPM, 0x200/Temp). Expect two signal rows.
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
        await sut.RebuildSignalsAsync();

        // v3.14.3 PATCH: both signals appear (0x100 has frames, 0x200 has none).
        sut.Signals.Should().HaveCount(2);
        var row100 = sut.Signals[0];
        row100.CanIdHex.Should().Be("0x100");
        row100.SignalName.Should().Be("RPM");
        row100.Unit.Should().Be("rpm");
        row100.IsPlotted.Should().BeFalse();
        row100.FrameCount.Should().Be(2, "v3.14.3 PATCH: row tracks frame count from bucket");
        row100.LatestValue.Should().Be(322.0, "LatestValue must be the LAST decoded value");

        var row200 = sut.Signals[1];
        row200.CanIdHex.Should().Be("0x200");
        row200.SignalName.Should().Be("Temp");
        row200.Unit.Should().Be("C");
        row200.IsPlotted.Should().BeFalse();
        row200.FrameCount.Should().Be(0, "v3.14.3 PATCH: 0x200 row has 0 frames");
        row200.LatestValue.Should().Be(double.NaN,
            "v3.14.3 PATCH: LatestValue is NaN when no frames exist for the signal");
    }

    /// <summary>
    /// v3.11.0 T4 (H8) + v3.14.3 PATCH: when the bucket contains NO
    /// frames for ANY message's CAN ID, the signal table is NOT
    /// empty — it shows ALL DBC signals with FrameCount=0 and
    /// LatestValue=NaN. v3.14.3 PATCH fixes the v3.11.x behavior
    /// (which produced an empty Signals collection).
    /// </summary>
    [Fact]
    public async Task BuildSignalRows_NoMatchingFrames_AllDbcSignalsAppearWithZeroCount()
    {
        // Arrange: one source emitting only 0x555 frames. DBC defines
        // 0x100 and 0x200. Neither message has matching frames but
        // both STILL appear in the signal table (DBC-driven view).
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
        await sut.RebuildSignalsAsync();

        // v3.14.3 PATCH: both DBC signals appear with zero frames,
        // NOT skipped (which was the v3.11.x / v3.14.x behavior).
        sut.Signals.Should().HaveCount(2,
            "v3.14.3 PATCH: signal table shows ALL DBC signals regardless of frame presence");
        sut.Signals.Should().AllSatisfy(r =>
        {
            r.FrameCount.Should().Be(0, "no frames in bucket for any DBC signal");
            r.LatestValue.Should().Be(double.NaN, "no frames to decode → NaN sentinel");
            r.IsPlotted.Should().BeFalse("opt-in is a user action");
        });
    }

    /// <summary>
    /// v3.14.3 PATCH: chart series is NOT auto-built at load time.
    /// Users opt-in via <c>TogglePlot</c>. This test pins the new
    /// behavior: at load time <c>ChartViewModel.Series</c> is empty;
    /// after <c>TogglePlot(row)</c> it contains one series per source
    /// with matching frames.
    /// </summary>
    [Fact]
    public async Task BuildChartSeries_OneSourceOneSignal_AddsOneSeries_OnOptIn()
    {
        // Arrange: one source emitting 0x100 frames. DBC has one
        // signal on 0x100 (RPM).
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
        await sut.RebuildSignalsAsync();

        // v3.14.3 PATCH: chart is empty until user opts in.
        sut.ChartViewModel.Series.Should().BeEmpty(
            "v3.14.3 PATCH: chart series is NOT auto-built; user opts in per-signal");
        var row = sut.Signals.Single(r => r.CanIdHex == "0x100" && r.SignalName == "RPM");
        row.IsPlotted.Should().BeFalse();

        // Act: opt in via SetPlotOptIn (production uses TogglePlot via the
        // DataGrid checkbox Click handler; tests use the explicit
        // SetPlotOptIn API to avoid binding-state assumptions).
        sut.SetPlotOptIn(row, true);

        // Assert: chart now has 1 series, fully populated.
        sut.ChartViewModel.Series.Should().ContainSingle();
        var series = sut.ChartViewModel.Series[0];
        series.SourceId.Should().Be("a");
        series.Unit.Should().Be("rpm");
        series.Color.Should().Be(OxyColors.Blue);
        series.DisplayName.Should().Contain("RPM");
        series.IsPlotPending.Should().BeFalse("v3.14.3 PATCH: opt-in fully populates the series (no placeholder)");
        series.YValues.Should().Equal(16.0, 32.0);
        // Note: SetPlotOptIn does NOT touch row.IsPlotted (the TwoWay
        // binding does, in production XAML). Tests verify the chart-
        // side effect; the row.IsPlotted binding behavior is covered
        // indirectly via the XAML integration smoke test in step 10.
    }

    /// <summary>
    /// v3.13.2 PATCH F5 + v3.14.3 PATCH: when a DBC is loaded via
    /// <see cref="DbcService.DbcLoaded"/>, the Trace Viewer rebuilds
    /// its Signals collection (DBC changed → different signal catalog).
    /// v3.14.3 PATCH: rebuild now emits ALL DBC signals regardless of
    /// frame presence; the chart area is NOT auto-populated.
    /// </summary>
    [Fact]
    public void DbcService_DbcLoaded_Fires_RebuildSignalsCore()
    {
        // Arrange: one source emitting 0x100 frames. No DBC loaded yet.
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
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());
        sut.Signals.Should().BeEmpty("no DBC loaded yet at TVM construction time");

        // Act: install the DBC + raise DbcLoaded.
        dbc.SetCurrentForTests(DocWithTwoMessages());
        dbc.GetType().GetEvent(nameof(DbcService.DbcLoaded))!
            .RaiseMethod(dbc, DocWithTwoMessages());

        // v3.14.3 PATCH: rebuild emits ALL DBC signals (2 in this DBC).
        sut.Signals.Should().HaveCount(2,
            "v3.14.3 PATCH: rebuild emits all DBC signals regardless of frame presence");
        sut.Signals[0].CanIdHex.Should().Be("0x100");
        sut.Signals[0].SignalName.Should().Be("RPM");
        sut.Signals[1].CanIdHex.Should().Be("0x200");
        sut.Signals[1].SignalName.Should().Be("Temp");

        // v3.14.3 PATCH: chart is empty (DBC change wipes user opt-ins,
        // and no opt-ins were set before this DbcLoaded).
        sut.ChartViewModel.Series.Should().BeEmpty(
            "v3.14.3 PATCH: chart is empty after DbcLoaded (no auto-build)");
    }

    /// <summary>
    /// v3.14.3 PATCH: after the user opts a signal in (TogglePlot),
    /// subsequent ASC additions (SourcesChanged) must preserve the
    /// opt-in — the user's intentional choice survives the noise of
    /// adding more sources. RefreshFrameCounts updates FrameCount +
    /// LatestValue in place; RemoveOrphanChartSeries handles unloaded
    /// sources. This test pins the survival contract.
    /// </summary>
    [Fact]
    public async Task OnRegistrySourcesChanged_AfterOptIn_PreservesChartSeriesAndIsPlotted()
    {
        // Arrange: one source emitting 0x100 frames.
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
        await sut.RebuildSignalsAsync();

        // Opt in to 0x100/RPM via SetPlotOptIn + manually flip IsPlotted to
        // mimic the XAML TwoWay binding (SetPlotOptIn itself only does
        // the chart-side work; the binding owns IsPlotted in production).
        var row = sut.Signals.Single(r => r.CanIdHex == "0x100" && r.SignalName == "RPM");
        row.IsPlotted = true;
        sut.SetPlotOptIn(row, true);
        sut.ChartViewModel.Series.Should().ContainSingle();

        // Act: simulate a 2nd ASC load by re-raising SourcesChanged
        // (the existing source's frames are still there; SourcesChanged
        // fires every time the registry mutates).
        registry.SourcesChanged += Raise.Event<Action>();

        // Assert: opt-in survived. Series is still 1; row still flagged.
        row.IsPlotted.Should().BeTrue("v3.14.3 PATCH: opt-in survives SourcesChanged");
        sut.ChartViewModel.Series.Should().ContainSingle(
            "v3.14.3 PATCH: chart series survives SourcesChanged (was wiped in pre-v3.14.3)");
    }
}