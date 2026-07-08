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
/// v3.15.0 MINOR: pins the watch-list behavior. The v3.14.x
/// "DBC 全列" tests have been rewritten: the watch list is empty
/// by default and only grows when the user explicitly calls
/// <c>AddToWatch</c>. <c>RefreshFrameCounts</c> updates the
/// per-row frame count in place. <c>LoadedDbcPath</c> tracks the
/// loaded DBC file (B1 fix).
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
            Path.Combine(Path.GetTempPath(), $"tmtrace-vm-watch-{Guid.NewGuid():N}.tmtrace"),
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
    public async Task WatchedSignals_DefaultsToEmpty_OnLoad()
    {
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

        // v3.15.0 MINOR: watch list is empty by default (placeholder
        // is filtered out of the real count).
        sut.WatchedSignals.Where(w => !w.IsPlaceholder).Should().BeEmpty(
            "v3.15.0 MINOR: watch list is empty until user explicitly AddToWatch");
        sut.ChartViewModel.Series.Should().BeEmpty(
            "v3.15.0 MINOR: chart is empty (no watched signals to plot)");
    }

    [Fact]
    public async Task AddToWatch_AppendsRowAndPlotsChartSeries()
    {
        var registry = MakeFakeRegistry();
        var svc = MakeFakeService();
        registry.Sources.Returns(new List<TraceSource>
        {
            new("a", "traceA", "C:/a.asc", OxyColors.Blue, LineStyle.Solid),
        });
        registry.GetService("a").Returns(svc);
        registry.GetFrames("a").Returns(new[] { Frame(0x100, 0x10, 0x00) });

        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        dbc.SetCurrentForTests(DocWithTwoMessages());
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());
        await sut.RebuildSignalsAsync();

        sut.AddToWatch(0x100, "RPM", "");

        sut.WatchedSignals.Where(w => !w.IsPlaceholder).Should().ContainSingle()
            .Which.SignalName.Should().Be("RPM");
        sut.ChartViewModel.Series.Should().ContainSingle();
    }

    [Fact]
    public async Task RemoveFromWatch_UnplotsAndRemovesRow()
    {
        var registry = MakeFakeRegistry();
        var svc = MakeFakeService();
        registry.Sources.Returns(new List<TraceSource>
        {
            new("a", "traceA", "C:/a.asc", OxyColors.Blue, LineStyle.Solid),
        });
        registry.GetService("a").Returns(svc);
        registry.GetFrames("a").Returns(new[] { Frame(0x100, 0x10, 0x00) });

        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        dbc.SetCurrentForTests(DocWithTwoMessages());
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());
        await sut.RebuildSignalsAsync();

        sut.AddToWatch(0x100, "RPM", "");
        var row = sut.WatchedSignals.Single(w => !w.IsPlaceholder);
        sut.RemoveFromWatch(row);

        sut.WatchedSignals.Where(w => !w.IsPlaceholder).Should().BeEmpty();
        sut.ChartViewModel.Series.Should().BeEmpty();
        sut.WatchedSignals.Should().Contain(w => w.IsPlaceholder,
            "v3.15.0 MINOR: placeholder row reappears when watch list is empty");
    }

    [Fact]
    public async Task AddToWatch_DuplicateSignal_IsIdempotent()
    {
        var registry = MakeFakeRegistry();
        var svc = MakeFakeService();
        registry.Sources.Returns(new List<TraceSource>
        {
            new("a", "traceA", "C:/a.asc", OxyColors.Blue, LineStyle.Solid),
        });
        registry.GetService("a").Returns(svc);
        registry.GetFrames("a").Returns(new[] { Frame(0x100, 0x10, 0x00) });

        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        dbc.SetCurrentForTests(DocWithTwoMessages());
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());
        await sut.RebuildSignalsAsync();

        sut.AddToWatch(0x100, "RPM", "");
        sut.AddToWatch(0x100, "RPM", "");

        sut.WatchedSignals.Where(w => !w.IsPlaceholder).Should().ContainSingle(
            "v3.15.0 MINOR: duplicate AddToWatch is idempotent");
        sut.ChartViewModel.Series.Should().ContainSingle();
    }

    [Fact]
    public async Task RefreshFrameCounts_UpdatesWatchedRowsInPlace()
    {
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

        sut.AddToWatch(0x100, "RPM", "");
        var row = sut.WatchedSignals.Single(w => !w.IsPlaceholder);
        row.FrameCount.Should().Be(2);
        row.LatestValue.Should().Be(32.0);
    }

    [Fact]
    public void OnDbcLoaded_UpdatesLoadedDbcPath()
    {
        var registry = MakeFakeRegistry();
        var dbc = new DbcService(NullLogger<DbcService>.Instance);
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());

        var docWithPath = DocWithTwoMessages() with { SourcePath = "C:/test/foo.dbc" };
        dbc.SetCurrentForTests(docWithPath);
        dbc.GetType().GetEvent(nameof(DbcService.DbcLoaded))!
            .RaiseMethod(dbc, docWithPath);

        sut.LoadedDbcPath.Should().Be("C:/test/foo.dbc");
        sut.LoadedDbcPathDisplay.Should().Be("foo.dbc");
    }

    [Fact]
    public async Task EnsurePlaceholderRow_OnDbcLoadedNoAsc_ShowsLoadAscPlaceholder()
    {
        var registry = MakeFakeRegistry();
        var svc = MakeFakeService();
        registry.Sources.Returns(new List<TraceSource>
        {
            new("a", "traceA", "C:/a.asc", OxyColors.Blue, LineStyle.Solid),
        });
        registry.GetService("a").Returns(svc);
        registry.GetFrames("a").Returns(new[] { Frame(0x100, 0x10, 0x00) });

        var dbc = new DbcService(NullLogger<DbcService>.Instance);
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());
        await sut.RebuildSignalsAsync();
        // No DBC loaded yet — placeholder shows "no DBC and no .asc loaded".
        sut.WatchedSignals.Should().Contain(w => w.IsPlaceholder);
    }

    [Fact]
    public async Task EnsurePlaceholderRow_DbcLoadedNoAsc_ShowsAddToWatchPlaceholder()
    {
        var registry = MakeFakeRegistry();
        var svc = MakeFakeService();
        registry.Sources.Returns(new List<TraceSource>
        {
            new("a", "traceA", "C:/a.asc", OxyColors.Blue, LineStyle.Solid),
        });
        registry.GetService("a").Returns(svc);
        registry.GetFrames("a").Returns(new[] { Frame(0x100, 0x10, 0x00) });

        var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
        dbc.SetCurrentForTests(DocWithTwoMessages());
        var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());
        await sut.RebuildSignalsAsync();

        // DBC loaded, no AddToWatch yet → placeholder with add-to-watch hint.
        sut.WatchedSignals.Where(w => !w.IsPlaceholder).Should().BeEmpty();
        var placeholder = sut.WatchedSignals.Single(w => w.IsPlaceholder);
        placeholder.MessageName.Should().Contain("Add to watch");
    }
}