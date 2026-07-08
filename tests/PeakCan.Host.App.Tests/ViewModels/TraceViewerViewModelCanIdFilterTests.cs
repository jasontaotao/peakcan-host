using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OxyPlot;
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
/// v3.15.0 MINOR: global CanIdFilter tests rewritten for watch-list
/// mode. Filter restricts the bucket the watched rows see, so
/// FrameCount updates accordingly. The watch list itself does not
/// change (user's intent).
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

    private static TraceSessionLibrary MakeFakeSessionLibrary()
        => new TraceSessionLibrary(
            Path.Combine(Path.GetTempPath(), $"tmtrace-vm-filter-{Guid.NewGuid():N}.tmtrace"),
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
    public async Task CanIdFilter_NarrowsFrameCount_OnWatchedRows()
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
        sut.AddToWatch(0x100, "RPM", "");
        sut.AddToWatch(0x200, "Temp", "");

        // No filter: both rows have FrameCount = 1.
        var rpmRow = sut.WatchedSignals.First(w => w.SignalName == "RPM");
        var tempRow = sut.WatchedSignals.First(w => w.SignalName == "Temp");
        rpmRow.FrameCount.Should().Be(1);
        tempRow.FrameCount.Should().Be(1);

        // Act: filter to 0x100.
        sut.CanIdFilter = "0x100";

        rpmRow.FrameCount.Should().Be(1, "0x100 frame survives filter");
        tempRow.FrameCount.Should().Be(0, "0x200 frame excluded by 0x100-only filter");
    }

    [Fact]
    public async Task ClearCanIdFilterCommand_RestoresFrameCount()
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
        sut.AddToWatch(0x100, "RPM", "");
        sut.AddToWatch(0x200, "Temp", "");

        sut.CanIdFilter = "0x100";
        sut.ClearCanIdFilterCommand.Execute(null);

        sut.CanIdFilter.Should().BeEmpty();
        sut.WatchedSignals.First(w => w.SignalName == "RPM").FrameCount.Should().Be(1);
        sut.WatchedSignals.First(w => w.SignalName == "Temp").FrameCount.Should().Be(1);
    }

    [Fact]
    public async Task PerSourceFilter_OverridesGlobalFilter_OnWatchedRow()
    {
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
        sut.AddToWatch(0x100, "RPM", "");
        sut.AddToWatch(0x200, "Temp", "");

        // Set global filter to 0x100; source A per-source to 0x200.
        sut.CanIdFilter = "0x100";
        srcA.CanIdFilter = "0x200";

        var rpmRow = sut.WatchedSignals.First(w => w.SignalName == "RPM");
        var tempRow = sut.WatchedSignals.First(w => w.SignalName == "Temp");
        rpmRow.FrameCount.Should().Be(1, "source A excluded → only B contributes 0x100");
        tempRow.FrameCount.Should().Be(1, "source A only contributes 0x200");
    }

    [Fact]
    public async Task PerSourceFilter_InvalidTokens_AreSilentlySkipped()
    {
        var registry = MakeFakeRegistry();
        var svc = MakeFakeService();
        var src = new TraceSource("a", "traceA", "C:/a.asc", OxyColors.Blue, LineStyle.Solid);
        registry.Sources.Returns(new List<TraceSource> { src });
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
        sut.AddToWatch(0x100, "RPM", "");
        sut.AddToWatch(0x200, "Temp", "");

        // Invalid token "xyz" is dropped; 256 (=0x100) and 0x200 survive.
        src.CanIdFilter = "256, xyz, 0x200";

        var rpmRow = sut.WatchedSignals.First(w => w.SignalName == "RPM");
        var tempRow = sut.WatchedSignals.First(w => w.SignalName == "Temp");
        rpmRow.FrameCount.Should().Be(1);
        tempRow.FrameCount.Should().Be(1);
    }
}