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
using PeakCan.Host.App.Services.AnalysisApiKey;

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
    public async Task AddToWatch_OneSource_AddsOneSeries()
    {
        var registry = MakeFakeRegistry();
        var svc = MakeFakeService();
        registry.Sources.Returns(new List<TraceSource>
        {
            new("a", "traceA", "C:/a.asc", OxyColors.Blue, LineStyle.Solid),
        });
        registry.GetService("a").Returns(svc);
        registry.GetFrames("a").Returns(new[] { Frame(0x100, 0x42, 0x01) });

        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        dbc.SetCurrentForTests(DocWithRpmSignal());
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary(), apiKeyManager: new PeakCan.Host.App.Services.AnalysisApiKey.ApiKeyManager(
            Substitute.For<PeakCan.Host.Core.Analysis.ICredentialStore>(),
            Substitute.For<Microsoft.Extensions.Logging.ILogger<PeakCan.Host.App.Services.AnalysisApiKey.ApiKeyManager>>()));

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
            new("a", "traceA", "C:/a.asc", OxyColors.Blue, LineStyle.Solid),
            new("b", "traceB", "C:/b.asc", OxyColors.Orange, LineStyle.Dash),
        });
        registry.GetService("a").Returns(svcA);
        registry.GetService("b").Returns(svcB);
        registry.GetFrames("a").Returns(new[] { Frame(0x100, 0x10, 0x00) });
        registry.GetFrames("b").Returns(new[] { Frame(0x100, 0x20, 0x00) });

        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        dbc.SetCurrentForTests(DocWithRpmSignal());
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary(), apiKeyManager: new PeakCan.Host.App.Services.AnalysisApiKey.ApiKeyManager(
            Substitute.For<PeakCan.Host.Core.Analysis.ICredentialStore>(),
            Substitute.For<Microsoft.Extensions.Logging.ILogger<PeakCan.Host.App.Services.AnalysisApiKey.ApiKeyManager>>()));

        await sut.RebuildSignalsAsync();
        sut.AddToWatch(0x100, "RPM", "");

        sut.ChartViewModel.Series.Should().HaveCount(2);
        var styles = sut.ChartViewModel.Series
            .Select(s => s.PlotModel!.Series.OfType<LineSeries>().Single().LineStyle)
            .ToList();
        styles.Should().Contain(LineStyle.Solid);
        styles.Should().Contain(LineStyle.Dash);
    }

    [Fact]
    public async Task AddToWatch_SyncYAxes_SharedAcrossSeries()
    {
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
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary(), apiKeyManager: new PeakCan.Host.App.Services.AnalysisApiKey.ApiKeyManager(
            Substitute.For<PeakCan.Host.Core.Analysis.ICredentialStore>(),
            Substitute.For<Microsoft.Extensions.Logging.ILogger<PeakCan.Host.App.Services.AnalysisApiKey.ApiKeyManager>>()));

        await sut.RebuildSignalsAsync();
        sut.AddToWatch(0x100, "RPM", "");

        var yA = sut.ChartViewModel.Series[0].PlotModel!.Axes
            .OfType<OxyPlot.Axes.LinearAxis>()
            .First(a => a.Position == OxyPlot.Axes.AxisPosition.Left);
        var yB = sut.ChartViewModel.Series[1].PlotModel!.Axes
            .OfType<OxyPlot.Axes.LinearAxis>()
            .First(a => a.Position == OxyPlot.Axes.AxisPosition.Left);
        yA.Minimum.Should().BeApproximately(yB.Minimum, 0.001);
        yA.Maximum.Should().BeApproximately(yB.Maximum, 0.001);
    }
}