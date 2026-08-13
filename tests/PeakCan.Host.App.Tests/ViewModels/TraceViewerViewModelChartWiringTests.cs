using System.Collections.ObjectModel;
using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ScottPlot;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.ViewModels;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.Replay;
using Xunit;
using FrameFlags = PeakCan.HIL.Core.FrameFlags;
using ValueType = PeakCan.HIL.Core.Dbc.ValueType;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// v3.15.0 MINOR: chart wiring tests rewritten for watch-list mode.
/// Chart series are created via <c>AddToWatch</c>, not via
/// <c>RebuildSignalsAsync</c>. Each watch row can fan out to one
/// chart series per source that has matching frames.
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

    private static TraceSessionLibrary MakeFakeSessionLibrary()
        => new TraceSessionLibrary(
            Path.Combine(Path.GetTempPath(), $"tmtrace-vm-chart-{Guid.NewGuid():N}.tmtrace"),
            NullLogger<TraceSessionLibrary>.Instance);

    // v3.x (会话状态剥离 Task 3): ITraceSessionService 替身——配置非空集合，否则
    // VM ctor 对 WatchedSignals.CollectionChanged 的订阅会 NRE。
    private static ITraceSessionService MakeFakeSession()
    {
        var session = Substitute.For<ITraceSessionService>();
        session.WatchedSignals.Returns(new ObservableCollection<WatchedSignalRow>());
        session.SignalGroups.Returns(new ObservableCollection<WatchedSignalGroup>());
        return session;
    }

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

    // Timestamp defaults to 0.0 for legacy callers; tests that assert
    // "last frame wins" (RefreshFrameCounts picks MaxBy(Timestamp)) must
    // pass strictly increasing timestamps — MaxBy returns the FIRST
    // element on ties, so all-equal timestamps would decode frame 0.
    private static ReplayFrame Frame(uint id, params byte[] data) => Frame(id, 0.0, data);

    private static ReplayFrame Frame(uint id, double timestamp, params byte[] data) =>
        new(Timestamp: timestamp, Id: id, Dlc: (byte)data.Length,
            Data: data, Flags: FrameFlags.None);

    [Fact]
    public async Task AddToWatch_OneSource_AddsOneSeries()
    {
        var registry = MakeFakeRegistry();
        var svc = MakeFakeService();
        registry.Sources.Returns(new List<TraceSource>
        {
            new("a", "traceA", "C:/a.asc", Colors.Blue, new LineStyle()),
        });
        registry.GetService("a").Returns(svc);
        registry.GetFrames("a").Returns(new[] { Frame(0x100, 0x42, 0x01) });

        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        dbc.SetCurrentForTests(DocWithRpmSignal());
        var sut = new TraceViewerViewModel(MakeFakeSession(),registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());

        await sut.RebuildSignalsAsync();
        sut.ChartViewModel.Series.Should().BeEmpty();
        sut.AddToWatch(0x100, "RPM", "");
        sut.ChartViewModel.Series.Should().ContainSingle();
    }

    [Fact]
    public async Task AddToWatch_TwoSourcesSameSignal_CreatesTwoSeriesWithDistinctStrokes()
    {
        var registry = MakeFakeRegistry();
        var svcA = MakeFakeService();
        var svcB = MakeFakeService();
        registry.Sources.Returns(new List<TraceSource>
        {
            new("a", "traceA", "C:/a.asc", Colors.Blue, new LineStyle()),
            new("b", "traceB", "C:/b.asc", Colors.Orange, new LineStyle { Pattern = LinePattern.Dashed }),
        });
        registry.GetService("a").Returns(svcA);
        registry.GetService("b").Returns(svcB);
        registry.GetFrames("a").Returns(new[] { Frame(0x100, 0x10, 0x00) });
        registry.GetFrames("b").Returns(new[] { Frame(0x100, 0x20, 0x00) });

        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        dbc.SetCurrentForTests(DocWithRpmSignal());
        var sut = new TraceViewerViewModel(MakeFakeSession(),registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());

        await sut.RebuildSignalsAsync();
        sut.AddToWatch(0x100, "RPM", "");

        sut.ChartViewModel.Series.Should().HaveCount(2);
        // v3.62.0: stroke style lives on TraceSource.StrokeStyle (a LineStyle
        // class instance). LineStyle is a reference type without value
        // equality, so assert on its Pattern struct property instead.
        // Each series carries its source reference.
        var patterns = sut.ChartViewModel.Series
            .Select(s => s.Source!.StrokeStyle.Pattern)
            .ToList();
        patterns.Should().Contain(LinePattern.Solid);
        patterns.Should().Contain(LinePattern.Dashed);
    }

    [Fact]
    public async Task AddToWatch_SyncYAxes_SharedAcrossSeries()
    {
        var registry = MakeFakeRegistry();
        var svcA = MakeFakeService();
        var svcB = MakeFakeService();
        registry.Sources.Returns(new List<TraceSource>
        {
            new("a", "traceA", "C:/a.asc", Colors.Blue, new LineStyle()),
            new("b", "traceB", "C:/b.asc", Colors.Orange, new LineStyle { Pattern = LinePattern.Dashed }),
        });
        registry.GetService("a").Returns(svcA);
        registry.GetService("b").Returns(svcB);
        registry.GetFrames("a").Returns(new[]
        {
            Frame(0x100, 0.0, 0x10, 0x00), Frame(0x100, 1.0, 0x20, 0x00),
        });
        registry.GetFrames("b").Returns(new[]
        {
            Frame(0x100, 0.0, 0x30, 0x00), Frame(0x100, 1.0, 0x40, 0x00),
        });

        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        dbc.SetCurrentForTests(DocWithRpmSignal());
        var sut = new TraceViewerViewModel(MakeFakeSession(),registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());

        await sut.RebuildSignalsAsync();
        sut.AddToWatch(0x100, "RPM", "");

        // Both series created (one per source) for the same SignalKey.
        sut.ChartViewModel.Series.Should().HaveCount(2);
        sut.ChartViewModel.Series[0].Source.Should().NotBeNull();
        sut.ChartViewModel.Series[1].Source.Should().NotBeNull();
        // Same SignalKey → both resolve to the same Plot via PlotResolver.
        sut.ChartViewModel.Series[0].SignalKey.Should().Be(sut.ChartViewModel.Series[1].SignalKey);
        // TODO: ScottPlot port — exact Y-axis min/max assertion removed during
        // migration. SyncYAxes now sets limits on the View-owned Plot (via
        // PlotResolver) after the progressive background fill completes, so
        // it can't be asserted synchronously in a VM-level unit test without
        // a mocked PlotResolver + pre-populated YValues.
    }
}