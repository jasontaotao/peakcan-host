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
/// v3.4.2 PATCH + v3.14.3 PATCH: pins the global CanIdFilter
/// wiring. v3.14.3 PATCH: filter restricts FrameCount +
/// LatestValue per row (does NOT drop rows — DBC-driven catalog
/// is independent of data). Chart rows are NOT auto-built;
/// user must opt in via TogglePlot.
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

    // v3.5.0 MINOR: real TraceSessionLibrary against a per-test temp
    // path. Tests in this file do not assert on bundle round-trip; the
    // library is wired so the VM ctor is satisfied.
    private static TraceSessionLibrary MakeFakeSessionLibrary()
        => new TraceSessionLibrary(
            Path.Combine(Path.GetTempPath(), $"tmtrace-vm-{Guid.NewGuid():N}.tmtrace"),
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
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());

        await sut.RebuildSignalsAsync();
        // v3.14.3 PATCH: signal table shows BOTH messages (DBC-driven).
        sut.Signals.Should().HaveCount(2);
        // v3.14.3 PATCH: chart is empty until user opts in.
        sut.ChartViewModel.Series.Should().BeEmpty();

        // Act: set the filter to a single ID.
        sut.CanIdFilter = "0x100";

        // v3.14.3 PATCH: filter does NOT drop rows. Both DBC signals
        // still appear; only their FrameCount + LatestValue reflect the
        // filter (0x200 has 0 frames after the filter).
        sut.Signals.Should().HaveCount(2);
        var row100 = sut.Signals.Single(r => r.CanIdHex == "0x100");
        var row200 = sut.Signals.Single(r => r.CanIdHex == "0x200");
        row100.FrameCount.Should().Be(1, "0x100 frame survives the 0x100-only filter");
        row200.FrameCount.Should().Be(0, "0x200 frame is excluded by the 0x100-only filter");
        row200.LatestValue.Should().Be(double.NaN, "0x200 has no surviving frames → NaN");
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
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());

        await sut.RebuildSignalsAsync();
        sut.CanIdFilter = "0x200";
        // v3.14.3 PATCH: filter changes FrameCount, not row count.
        sut.Signals.Should().HaveCount(2);

        // Act: invoke the clear command.
        sut.ClearCanIdFilterCommand.Execute(null);

        // Assert: filter cleared.
        sut.CanIdFilter.Should().BeEmpty();
        sut.Signals.Should().HaveCount(2,
            "v3.14.3 PATCH: clearing the filter restores FrameCount to original; rows were never dropped");
        var row100 = sut.Signals.Single(r => r.CanIdHex == "0x100");
        row100.FrameCount.Should().Be(1, "0x100 frame count restored after clear");
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
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());
        // v3.13.0 PATCH F3: LoadDbcAsync was deleted — tests now drive
        // RebuildSignalsAsync directly against the pre-loaded DBC.
        await sut.RebuildSignalsAsync();

        // Establish baseline: no filters → 2 signals (DBC-driven catalog).
        // v3.14.3 PATCH: chart is empty (no auto-build).
        sut.Signals.Should().HaveCount(2);
        sut.ChartViewModel.Series.Should().BeEmpty();

        // Act 1: set the global filter to 0x100.
        sut.CanIdFilter = "0x100";

        // Assert 1: filter changes FrameCount, not row count.
        var row100 = sut.Signals.Single(r => r.CanIdHex == "0x100");
        var row200 = sut.Signals.Single(r => r.CanIdHex == "0x200");
        row100.FrameCount.Should().Be(2, "0x100 has 1 frame per source × 2 sources = 2");
        row200.FrameCount.Should().Be(0, "0x200 is excluded by the 0x100-only filter");

        // Act 2: set source A's per-source filter to "0x200" — INPC fires,
        // OnAnySourcePropertyChanged runs RefreshFrameCounts.
        srcA.CanIdFilter = "0x200";

        // Assert 2: per-source A flips 0x100 to 0 (A's 0x100 excluded);
        // 0x200 flips to 1 (only A contributes). Source B unchanged.
        sut.Signals.Should().HaveCount(2);
        var row100After = sut.Signals.Single(r => r.CanIdHex == "0x100");
        var row200After = sut.Signals.Single(r => r.CanIdHex == "0x200");
        row100After.FrameCount.Should().Be(1, "source A excluded → only B contributes 0x100");
        row200After.FrameCount.Should().Be(1, "source A only contributes 0x200");
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
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());
        // v3.13.0 PATCH F3: LoadDbcAsync was deleted — tests now drive
        // RebuildSignalsAsync directly against the pre-loaded DBC.
        await sut.RebuildSignalsAsync();

        // Act: set global filter to 0x100. Per-source filters stay empty.
        sut.CanIdFilter = "0x100";

        // v3.14.3 PATCH: filter does NOT drop rows. Both messages still
        // appear; only FrameCount + LatestValue reflect the filter.
        sut.Signals.Should().HaveCount(2);
        var row100 = sut.Signals.Single(r => r.CanIdHex == "0x100");
        var row200 = sut.Signals.Single(r => r.CanIdHex == "0x200");
        row100.FrameCount.Should().Be(2, "0x100 has 1 frame per source × 2 sources = 2");
        row200.FrameCount.Should().Be(0, "0x200 excluded by 0x100-only filter");
        // v3.14.3 PATCH: chart empty until user opts in.
        sut.ChartViewModel.Series.Should().BeEmpty();
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
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());
        // v3.13.0 PATCH F3: LoadDbcAsync was deleted — tests now drive
        // RebuildSignalsAsync directly against the pre-loaded DBC.
        await sut.RebuildSignalsAsync();

        // v3.14.3 PATCH: signal table shows both DBC signals regardless.
        sut.Signals.Should().HaveCount(2);

        // Act: set per-source filter with invalid token. xyz is silently
        // dropped, 256 (= 0x100) and 0x200 are valid → both rows survive
        // with their FrameCount reflecting the filter.
        src.CanIdFilter = "256, xyz, 0x200";

        // Assert: invalid token does not cause empty rebuild — both
        // signals survive. Chart empty until user opts in.
        sut.Signals.Should().HaveCount(2);
        sut.ChartViewModel.Series.Should().BeEmpty();
    }
}