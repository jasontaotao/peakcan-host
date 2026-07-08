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
/// v3.4.0 MINOR: pins the chart series production wiring that was
/// dead code in v3.2.0 / v3.3.x. After this MINOR,
/// <c>TraceViewerViewModel.RebuildSignalsAsync</c> populates
/// <c>ChartViewModel.Series</c> with one subplot per (source, signal)
/// pair, sets each subplot's LineSeries.LineStyle from the source's
/// <c>StrokeStyle</c>, and calls <c>SyncYAxes()</c> after population.
/// </summary>
public class TraceViewerViewModelChartWiringTests
{
    private static readonly IReadOnlyList<uint> EmptyFilter = System.Array.Empty<uint>();

    private static ITraceSessionRegistry MakeFakeRegistry()
    {
        var registry = Substitute.For<ITraceSessionRegistry>();
        registry.Sources.Returns(new List<TraceSource>());
        return registry;
    }

    private static DbcService MakeFakeDbcService()
        => Substitute.For<DbcService>(Substitute.For<ILogger<DbcService>>());

    private static ILogger<TraceViewerViewModel> MakeFakeLogger()
        => Substitute.For<ILogger<TraceViewerViewModel>>();

    // v3.5.0 MINOR: real TraceSessionLibrary against a per-test temp
    // path. Tests in this file do not assert on bundle round-trip; the
    // library is wired so the VM ctor is satisfied.
    private static TraceSessionLibrary MakeFakeSessionLibrary()
        => new TraceSessionLibrary(
            Path.Combine(Path.GetTempPath(), $"tmtrace-vm-{Guid.NewGuid():N}.tmtrace"),
            NullLogger<TraceSessionLibrary>.Instance);

    private static ITraceViewerService MakeFakeService()
    {
        var svc = Substitute.For<ITraceViewerService>();
        svc.TotalDuration.Returns(60.0);
        return svc;
    }

    private static DbcDocument DocWithRpmSignal() => DocWithSignals(
        id: 0x100, name: "M_RPM", signals: new[]
        {
            new Signal(Name: "RPM", StartBit: 0, Length: 16,
                       Order: ByteOrder.LittleEndian,
                       ValueType: ValueType.Unsigned,
                       Factor: 1.0, Offset: 0.0,
                       Min: 0, Max: 1000, Unit: "rpm",
                       Receivers: System.Array.Empty<string>()),
        });

    private static DbcDocument DocWithSignals(
        uint id, string name, IReadOnlyList<Signal> signals)
    {
        var msg = new Message(Id: id, Name: name, Dlc: 8, Sender: "ECU",
            Signals: signals, IsMultiplexed: false, MultiplexorSignalIndex: null);
        return new DbcDocument(
            Version: "", Nodes: System.Array.Empty<Node>(),
            Messages: new[] { msg },
            MessagesById: new Dictionary<uint, Message> { [id] = msg },
            ValueTables: new Dictionary<string, ValueTable>());
    }

    private static ReplayFrame Frame(uint id, params byte[] data) =>
        new(Timestamp: 0.0, Id: id, Dlc: (byte)data.Length,
            Data: data, Flags: FrameFlags.None);

    [Fact]
    public async Task RebuildSignalsAsync_AfterLoadDbc_PopulatesChartSeriesOnOptIn()
    {
        // v3.4.0 MINOR: was 0 chart series in v3.3.x (dead code). After
        // wiring, RebuildSignalsAsync produces 1 chart series per
        // (source, signal) pair — but ONLY after the user opts in via
        // the Plot checkbox (v3.14.3 PATCH).
        var registry = MakeFakeRegistry();
        var svcA = MakeFakeService();
        registry.Sources.Returns(new List<TraceSource>
        {
            new("a", "traceA", "C:/a.asc", OxyColors.Blue, LineStyle.Solid),
        });
        registry.GetService("a").Returns(svcA);
        registry.GetFrames("a").Returns(new[] { Frame(0x100, 0x42, 0x01) });

        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        dbc.SetCurrentForTests(DocWithRpmSignal());
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());

        await sut.RebuildSignalsAsync();

        // v3.14.3 PATCH: chart is empty at load time (opt-in only).
        sut.ChartViewModel.Series.Should().BeEmpty(
            "v3.14.3 PATCH: chart is NOT auto-built; user opts in via TogglePlot");
        var row = sut.Signals.Single();
        row.IsPlotted.Should().BeFalse();

        // Opt in → 1 chart series.
        sut.SetPlotOptIn(row, true);
        sut.ChartViewModel.Series.Should().ContainSingle();
    }

    [Fact]
    public async Task RebuildSignalsAsync_TwoSources_SameSignal_CreatesTwoSeriesWithDistinctStrokes_OnOptIn()
    {
        // v3.14.3 PATCH: same opt-in pattern, two sources with same
        // signal — opt-in creates one series per source, with the
        // source's stroke style.
        var registry = MakeFakeRegistry();
        var svcA = MakeFakeService();
        var svcB = MakeFakeService();
        registry.Sources.Returns(new List<TraceSource>
        {
            new("a", "traceA", "C:/a.asc", OxyColors.Blue, LineStyle.Solid),
            new("b", "traceB", "C:/b.asc", OxyColors.Orange, LineStyle.Dash),
        });
        registry.GetService("a").Returns(svcA);
        registry.GetService("b").Returns(svcB);
        registry.GetFrames("a").Returns(new[] { Frame(0x100, 0x10, 0x00) });
        registry.GetFrames("b").Returns(new[] { Frame(0x100, 0x20, 0x00) });

        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        dbc.SetCurrentForTests(DocWithRpmSignal());
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());

        await sut.RebuildSignalsAsync();

        // v3.14.3 PATCH: chart empty at load time.
        sut.ChartViewModel.Series.Should().BeEmpty();

        // Opt in → 2 series (one per source).
        var row = sut.Signals.Single();
        sut.SetPlotOptIn(row, true);
        sut.ChartViewModel.Series.Should().HaveCount(2);
        var styles = sut.ChartViewModel.Series
            .Select(s => s.PlotModel!.Series.OfType<LineSeries>().Single().LineStyle)
            .ToList();
        styles.Should().Contain(LineStyle.Solid);
        styles.Should().Contain(LineStyle.Dash);
    }

    [Fact]
    public async Task RebuildSignalsAsync_CallsSyncYAxesAfterPopulation()
    {
        // v3.4.0 MINOR: after the user opts in, the VM must call
        // SyncYAxes() so the chart renders with synchronized Y axes
        // across sources (v3.3.2 method).
        var registry = MakeFakeRegistry();
        var svcA = MakeFakeService();
        var svcB = MakeFakeService();
        registry.Sources.Returns(new List<TraceSource>
        {
            new("a", "traceA", "C:/a.asc", OxyColors.Blue, LineStyle.Solid),
            new("b", "traceB", "C:/b.asc", OxyColors.Orange, LineStyle.Dash),
        });
        registry.GetService("a").Returns(svcA);
        registry.GetService("b").Returns(svcB);
        registry.GetFrames("a").Returns(new[]
        {
            Frame(0x100, 0x10, 0x00), Frame(0x100, 0x20, 0x00),
        });
        registry.GetFrames("b").Returns(new[]
        {
            Frame(0x100, 0x30, 0x00), Frame(0x100, 0x40, 0x00),
        });

        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        dbc.SetCurrentForTests(DocWithRpmSignal());
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());

        await sut.RebuildSignalsAsync();

        // Opt in (single row covers both sources → 2 chart series).
        var row = sut.Signals.Single();
        sut.SetPlotOptIn(row, true);

        // Both subplots must have the same Y axis range (synchronized
        // by SignalKey via v3.3.2's SyncYAxes). Range A: 16..32,
        // Range B: 48..64; global 16..64; +5% padding → 13.6..66.4.
        var yA = sut.ChartViewModel.Series[0].PlotModel!.Axes
            .OfType<OxyPlot.Axes.LinearAxis>()
            .First(a => a.Position == OxyPlot.Axes.AxisPosition.Left);
        var yB = sut.ChartViewModel.Series[1].PlotModel!.Axes
            .OfType<OxyPlot.Axes.LinearAxis>()
            .First(a => a.Position == OxyPlot.Axes.AxisPosition.Left);
        yA.Minimum.Should().BeApproximately(yB.Minimum, 0.001);
        yA.Maximum.Should().BeApproximately(yB.Maximum, 0.001);
    }

    [Fact]
    public async Task RebuildSignalsAsync_NoSourcesLoaded_ChartSeriesEmpty()
    {
        var registry = MakeFakeRegistry();
        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        dbc.SetCurrentForTests(DocWithRpmSignal());
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());

        // v3.13.0 PATCH F3: LoadDbcAsync was deleted — tests now drive
        // RebuildSignalsAsync directly against the pre-loaded DBC.
        await sut.RebuildSignalsAsync();

        sut.ChartViewModel.Series.Should().BeEmpty();
    }
}
