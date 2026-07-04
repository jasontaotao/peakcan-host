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
}