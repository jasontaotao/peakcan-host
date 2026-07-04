using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;
using Xunit;
using ValueType = PeakCan.Host.Core.Dbc.ValueType;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// v1.5.1 PATCH Item 2 (Periodic DBC send): the periodic UI in
/// <c>DbcModeExpander</c> of <c>SendView.xaml</c> is backed by
/// <c>StartDbcCyclicCommand</c> + <c>StopDbcCyclicCommand</c> on
/// <see cref="DbcSendViewModel"/>. These tests pin the VM's command logic
/// without driving real timers or the PEAK SDK.
/// <para>
/// Uses a NSubstitute mock for <see cref="ICyclicDbcSendService"/> (the
/// VM takes the interface so test substitutes don't have to subclass
/// real <see cref="CyclicDbcSendService"/> + drive its timer). The
/// encoder + send service use the project's hand-fake patterns from
/// <c>DbcSendViewModelTests</c>.
/// </para>
/// </summary>
public class DbcSendViewModelCyclicTests
{
    /// <summary>Unsigned 8-bit signal in [0, 100] at bit offset 0.</summary>
    private static Signal MakeSignal(string name) =>
        new(name, 0, 8, ByteOrder.LittleEndian, ValueType.Unsigned, 1, 0, 0, 100, "", Array.Empty<string>());

    /// <summary>Hand-rolled SendService test seam (no WPF, no SDK).</summary>
    private sealed class FakeSendService : SendService
    {
        public FakeSendService() : base(NullLogger<SendService>.Instance) { }
        public override ValueTask<Result<Unit>> SendAsync(CanFrame frame, CancellationToken ct = default)
            => ValueTask.FromResult(Result<Unit>.Ok(default));
    }

    private static DbcDocument MakeDoc(params Message[] msgs) => new(
        "v1",
        Array.Empty<Node>(),
        msgs,
        new Dictionary<uint, Message>(msgs.Length),
        new Dictionary<string, ValueTable>());

    [Fact]
    public void StartDbcCyclic_WithoutSelectedMessage_DoesNothing()
    {
        var dbcService = new DbcService(NullLogger<DbcService>.Instance);
        var send = new FakeSendService();
        var cyclic = Substitute.For<ICyclicDbcSendService>();
        var sut = new DbcSendViewModel(new DbcEncodeService(), send, dbcService, cyclic, NullLogger<DbcSendViewModel>.Instance);

        sut.StartDbcCyclicCommand.Execute(null);

        cyclic.DidNotReceive().Start(Arg.Any<Func<(Message, IReadOnlyDictionary<string, double>)>>(), Arg.Any<TimeSpan>());
        sut.IsDbcCyclicRunning.Should().BeFalse();
    }

    [Fact]
    public void StartDbcCyclic_WithSelectedMessage_CallsService_AndSetsIsRunningTrue()
    {
        var msg = new Message(0x100u, "TestMsg", 8, "Test",
            new[] { MakeSignal("S") },
            IsMultiplexed: false, MultiplexorSignalIndex: null);
        var doc = MakeDoc(msg);
        var dbcService = new DbcService(NullLogger<DbcService>.Instance);
        dbcService.SetCurrentForTests(doc);
        var send = new FakeSendService();
        var cyclic = Substitute.For<ICyclicDbcSendService>();
        var sut = new DbcSendViewModel(new DbcEncodeService(), send, dbcService, cyclic, NullLogger<DbcSendViewModel>.Instance);

        sut.SelectedDbcMessage = doc.Messages[0];
        sut.DbcCyclicIntervalText = "100";
        sut.StartDbcCyclicCommand.Execute(null);

        // v1.5.1 PATCH Item 2 (review fix MEDIUM #1): the TextBox label
        // says "Cyclic interval (ms):" and the default value is "100".
        // The VM parses with int.TryParse + bounds 1..60000, converts to
        // TimeSpan.FromMilliseconds(100). Pre-fix this asserted on
        // TimeSpan.FromDays(100) (TimeSpan.TryParse silent footgun).
        cyclic.Received(1).Start(Arg.Any<Func<(Message, IReadOnlyDictionary<string, double>)>>(),
                                  Arg.Is<TimeSpan>(t => t == TimeSpan.FromMilliseconds(100)));
        sut.IsDbcCyclicRunning.Should().BeTrue();
    }

    [Fact]
    public void StopDbcCyclic_CallsService_AndSetsIsRunningFalse()
    {
        var msg = new Message(0x100u, "TestMsg", 8, "Test",
            new[] { MakeSignal("S") },
            IsMultiplexed: false, MultiplexorSignalIndex: null);
        var doc = MakeDoc(msg);
        var dbcService = new DbcService(NullLogger<DbcService>.Instance);
        dbcService.SetCurrentForTests(doc);
        var send = new FakeSendService();
        var cyclic = Substitute.For<ICyclicDbcSendService>();
        cyclic.IsRunning.Returns(true);
        var sut = new DbcSendViewModel(new DbcEncodeService(), send, dbcService, cyclic, NullLogger<DbcSendViewModel>.Instance);

        sut.SelectedDbcMessage = doc.Messages[0];
        sut.DbcCyclicIntervalText = "100";
        sut.StartDbcCyclicCommand.Execute(null);
        sut.IsDbcCyclicRunning.Should().BeTrue();

        sut.StopDbcCyclicCommand.Execute(null);

        cyclic.Received(1).Stop();
        sut.IsDbcCyclicRunning.Should().BeFalse();
    }

    [Fact]
    public void StartDbcCyclic_WithInvalidIntervalText_DoesNothing()
    {
        var msg = new Message(0x100u, "TestMsg", 8, "Test",
            new[] { MakeSignal("S") },
            IsMultiplexed: false, MultiplexorSignalIndex: null);
        var doc = MakeDoc(msg);
        var dbcService = new DbcService(NullLogger<DbcService>.Instance);
        dbcService.SetCurrentForTests(doc);
        var send = new FakeSendService();
        var cyclic = Substitute.For<ICyclicDbcSendService>();
        var sut = new DbcSendViewModel(new DbcEncodeService(), send, dbcService, cyclic, NullLogger<DbcSendViewModel>.Instance);

        sut.SelectedDbcMessage = doc.Messages[0];
        sut.DbcCyclicIntervalText = "not-a-timespan";
        sut.StartDbcCyclicCommand.Execute(null);

        cyclic.DidNotReceive().Start(Arg.Any<Func<(Message, IReadOnlyDictionary<string, double>)>>(), Arg.Any<TimeSpan>());
        sut.IsDbcCyclicRunning.Should().BeFalse();
    }

    [Fact]
    public void StartDbcCyclic_BuildsCurrentSignalValuesFromSignalRows()
    {
        var msg = new Message(0x100u, "TestMsg", 8, "Test",
            new[] { MakeSignal("S1"), MakeSignal("S2") },
            IsMultiplexed: false, MultiplexorSignalIndex: null);
        var doc = MakeDoc(msg);
        var dbcService = new DbcService(NullLogger<DbcService>.Instance);
        dbcService.SetCurrentForTests(doc);
        var send = new FakeSendService();
        var cyclic = Substitute.For<ICyclicDbcSendService>();
        var sut = new DbcSendViewModel(new DbcEncodeService(), send, dbcService, cyclic, NullLogger<DbcSendViewModel>.Instance);

        sut.SelectedDbcMessage = doc.Messages[0];
        // Set S1=10, leave S2 null
        sut.SignalRows[0].Value = 10;
        sut.DbcCyclicIntervalText = "100";

        // Capture the provider Func passed to Start so we can invoke it.
        Func<(Message, IReadOnlyDictionary<string, double>)>? captured = null;
        cyclic.WhenForAnyArgs(c => c.Start(Arg.Any<Func<(Message, IReadOnlyDictionary<string, double>)>>(), Arg.Any<TimeSpan>()))
              .Do(call => captured = call.ArgAt<Func<(Message, IReadOnlyDictionary<string, double>)>>(0));

        sut.StartDbcCyclicCommand.Execute(null);

        captured.Should().NotBeNull();
        var (returnedMsg, values) = captured!.Invoke();
        returnedMsg.Id.Should().Be(0x100u);
        values.Should().HaveCount(1, "only S1 has a value");
        values["S1"].Should().Be(10.0);
    }
}
