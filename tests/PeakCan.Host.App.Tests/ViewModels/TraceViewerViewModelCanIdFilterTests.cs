using FluentAssertions;
using Microsoft.Extensions.Logging;
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
/// v3.4.2 PATCH: pins the global CanIdFilter wiring against
/// <see cref="TraceViewerViewModel.RebuildSignalsAsync"/>. Setting the
/// filter must restrict the rebuilt Signals + ChartViewModel.Series to
/// frames whose CAN ID is in the parsed allow-set. Clearing restores
/// the unfiltered state.
/// </summary>
public class TraceViewerViewModelCanIdFilterTests
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

    [Fact]
    public async Task CanIdFilter_ValidHexId_SignalsOnlyIncludeMatchingCanId()
    {
        // Arrange: registry with one source emitting both 0x100 and 0x200 frames.
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
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger());

        // Load DBC → unfiltered rebuild (2 signals, 2 series).
        await sut.LoadDbcAsync("C:/fake.dbc");
        sut.Signals.Should().HaveCount(2);
        sut.ChartViewModel.Series.Should().HaveCount(2);

        // Act: set the filter to a single ID; rebuild should narrow to 1.
        sut.CanIdFilter = "0x100";

        // The setter is fully synchronous — partial change handler calls
        // RebuildSignalsCore inline, no Task continuation race.
        // Assert: only 0x100's signal row survives; only 0x100's chart series survives.
        sut.Signals.Should().HaveCount(1);
        sut.Signals[0].CanIdHex.Should().Be("0x100");
        sut.ChartViewModel.Series.Should().HaveCount(1);
    }

    [Fact]
    public async Task ClearCanIdFilterCommand_ResetsToEmpty_AllSignalsRestored()
    {
        // Arrange: same setup as above. Start filtered, then clear.
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
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger());

        await sut.LoadDbcAsync("C:/fake.dbc");
        sut.CanIdFilter = "0x200";
        sut.Signals.Should().HaveCount(1);

        // Act: invoke the clear command.
        sut.ClearCanIdFilterCommand.Execute(null);

        // Assert: filter cleared, all signals back.
        sut.CanIdFilter.Should().BeEmpty();
        sut.Signals.Should().HaveCount(2);
        sut.ChartViewModel.Series.Should().HaveCount(2);
    }

    // v3.4.3 PATCH: per-source filter overrides. The global filter applies
    // to all sources; a non-empty per-source filter on TraceSource.CanIdFilter
    // overrides the global for that one source. Empty per-source = inherit global.

    [Fact]
    public async Task PerSourceFilter_NonEmptyOverridesGlobal_OnlyMatchingFramesForThatSource()
    {
        // Arrange: two sources, each emitting 0x100 + 0x200. Global = "0x100".
        // Source A per-source = "0x200" → only 0x200 frames show for A.
        // Source B leaves per-source empty → inherits "0x100" → only 0x100.
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
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger());
        await sut.LoadDbcAsync("C:/fake.dbc");

        // Establish baseline: no filters → 2 signals (one per message) and
        // 4 chart series (2 sources × 2 messages × 1 signal each).
        sut.Signals.Should().HaveCount(2);
        sut.ChartViewModel.Series.Should().HaveCount(4);

        // Act 1: set the global filter to 0x100.
        sut.CanIdFilter = "0x100";

        // Assert 1: both sources now narrow to 0x100 only → 1 signal (RPM) and
        // 2 chart series (one per source, both RPM).
        sut.Signals.Should().HaveCount(1);
        sut.Signals[0].CanIdHex.Should().Be("0x100");
        sut.ChartViewModel.Series.Should().HaveCount(2);

        // Act 2: set source A's per-source filter to "0x200" — INPC fires,
        // RebuildSignalsCore runs, source A flips to 0x200 only.
        srcA.CanIdFilter = "0x200";

        // Assert 2: source A now shows 0x200 (Temp), source B still shows 0x100 (RPM).
        // byId sees both 0x100 (from B) and 0x200 (from A) → 2 signal rows.
        sut.Signals.Should().HaveCount(2);
        sut.ChartViewModel.Series.Should().HaveCount(2);
        var seriesIds = sut.ChartViewModel.Series
            .Select(s => s.SourceId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        seriesIds.Should().Equal("a", "b");
        // Source A's series must be the 0x200 series (Temp signal, Unit "C").
        // Source B's series must be the 0x100 series (RPM signal, Unit "rpm").
        var seriesA = sut.ChartViewModel.Series.Single(s => s.SourceId == "a");
        var seriesB = sut.ChartViewModel.Series.Single(s => s.SourceId == "b");
        seriesA.DisplayName.Should().Contain("Temp");
        seriesB.DisplayName.Should().Contain("RPM");
    }

    [Fact]
    public async Task PerSourceFilter_EmptyFallsBackToGlobal_AllGlobalMatchingFramesForThatSource()
    {
        // Arrange: two sources, each emitting 0x100 + 0x200. Global = "0x100".
        // Both sources leave per-source empty → both inherit global → only 0x100.
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
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger());
        await sut.LoadDbcAsync("C:/fake.dbc");

        // Act: set global filter to 0x100. Per-source filters stay empty.
        sut.CanIdFilter = "0x100";

        // Assert: both sources show 0x100 (RPM) only — 1 signal row, 2 chart series
        // (one per source, both RPM).
        sut.Signals.Should().HaveCount(1);
        sut.Signals[0].CanIdHex.Should().Be("0x100");
        sut.ChartViewModel.Series.Should().HaveCount(2);
        sut.ChartViewModel.Series.Should().OnlyContain(s => s.DisplayName.Contains("RPM"));
    }

    [Fact]
    public async Task PerSourceFilter_InvalidTokens_AreSilentlySkipped()
    {
        // Arrange: one source emitting 100 (decimal) + 0x200 frames.
        // Per-source filter = "100, xyz, 0x200" → xyz silently skipped → {100, 0x200}.
        var registry = MakeFakeRegistry();
        var svc = MakeFakeService();
        var src = new TraceSource("a", "traceA", "C:/a.asc", OxyColors.Blue, LineStyle.Solid);
        registry.Sources.Returns(new List<TraceSource> { src });
        registry.GetService("a").Returns(svc);
        registry.GetFrames("a").Returns(new[]
        {
            // 100 decimal → maps to 0x100 if DBC message id = 0x100 → "100" parses to 100 = 0x64,
            // which does NOT match 0x100. So we use 0x100 frames and a filter of
            // "256, xyz, 0x200" where 256 decimal = 0x100.
            Frame(0x100, 0x10, 0x00),
            Frame(0x200, 0x20, 0x00),
        });

        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        dbc.SetCurrentForTests(DocWithTwoMessages());
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger());
        await sut.LoadDbcAsync("C:/fake.dbc");

        // Baseline: 2 signals, 2 series.
        sut.Signals.Should().HaveCount(2);

        // Act: set per-source filter with invalid token. xyz is silently dropped,
        // 256 (= 0x100) and 0x200 are valid → both signals + both series survive.
        src.CanIdFilter = "256, xyz, 0x200";

        // Assert: invalid token does not cause empty rebuild — both messages survive.
        sut.Signals.Should().HaveCount(2);
        sut.ChartViewModel.Series.Should().HaveCount(2);
    }
}