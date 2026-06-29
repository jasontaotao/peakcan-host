using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;
using PeakCan.Host.Infrastructure.Channel;
using Xunit;
using ValueType = PeakCan.Host.Core.Dbc.ValueType;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// v1.4.0 MINOR Send DBC: <see cref="DbcSendViewModel"/> exposes a DBC
/// message dropdown + per-signal DataGrid editor + Send button. The VM
/// pulls messages from <see cref="DbcService.Current"/>, repopulates the
/// signal rows on selection, and on Send encodes values via
/// <see cref="DbcEncodeService"/> + dispatches the resulting
/// <see cref="CanFrame"/> through <see cref="SendService"/>.
/// <para>
/// Tests use a real <see cref="DbcService"/> (wired via the
/// <c>SetCurrentForTests</c> test seam — visible via <c>InternalsVisibleTo</c>)
/// and a hand-fake <see cref="FakeSendService"/> subclass (mirrors the
/// <c>SendViewModelTests.FakeSendService</c> pattern). NSubstitute
/// cannot mock <see cref="DbcService"/> because its ctor requires a
/// non-null <c>ILogger&lt;DbcService&gt;</c> and there is no
/// parameterless overload. No WPF Application is created, so no
/// STA-WPF xunit race (memory v1.2.11).
/// </para>
/// </summary>
public class DbcSendViewModelTests
{
    /// <summary>
    /// Build an Unsigned 8-bit signal at bit offset 0 with [0, 100] range,
    /// factor=1, offset=0 — the minimum needed for the VM to render
    /// <c>DisplayName</c> + <c>ValueType</c> and for the encoder to
    /// accept values in [0, 100].
    /// </summary>
    private static Signal MakeSignal(string name) =>
        new(name, 0, 8, ByteOrder.LittleEndian, ValueType.Unsigned, 1, 0, 0, 100, "", Array.Empty<string>());

    /// <summary>
    /// In-memory fake of <see cref="SendService"/>. Mirrors the project
    /// hand-fake style for App-layer VMs (see
    /// <c>SendViewModelTests.FakeSendService</c>). Captures the last
    /// frame so the test can assert on encoded payload.
    /// </summary>
    private sealed class FakeSendService : SendService
    {
        public FakeSendService() : base(NullLogger<SendService>.Instance) { }
        public CanFrame? LastFrame { get; private set; }
        public Result<Unit> NextResult { get; set; } = Result<Unit>.Ok(default);

        public override ValueTask<Result<Unit>> SendAsync(CanFrame frame, CancellationToken ct = default)
        {
            LastFrame = frame;
            return ValueTask.FromResult(NextResult);
        }
    }

    private static DbcDocument MakeDoc(params Message[] msgs) => new(
        "v1",
        Array.Empty<Node>(),
        msgs,
        new Dictionary<uint, Message>(msgs.Length),
        new Dictionary<string, ValueTable>());

    /// <summary>
    /// v1.4.0 MINOR: selecting a DBC message populates <c>SignalRows</c>
    /// with one <see cref="DbcSignalRowViewModel"/> per signal. The VM
    /// must clear the previous rows on switch so stale signal rows from
    /// a different message do not leak into the new selection.
    /// </summary>
    [Fact]
    public void SelectedDbcMessage_PopulatesSignalRows()
    {
        var doc = MakeDoc(
            new Message(0x100u, "TestMsg", 8, "Test",
                new[] { MakeSignal("Sig1"), MakeSignal("Sig2") },
                IsMultiplexed: false, MultiplexorSignalIndex: null));
        // DbcService is a concrete class with a non-default ctor
        // (ILogger<DbcService>) and no parameterless overload, so
        // NSubstitute cannot auto-mock it. Use the production ctor +
        // SetCurrentForTests test seam (visible via InternalsVisibleTo).
        var dbcService = new DbcService(NullLogger<DbcService>.Instance);
        dbcService.SetCurrentForTests(doc);
        var sendService = new FakeSendService();
        var sut = new DbcSendViewModel(new DbcEncodeService(), sendService, dbcService);

        sut.SelectedDbcMessage = doc.Messages[0];

        sut.SignalRows.Should().HaveCount(2);
        sut.SignalRows[0].Signal.Name.Should().Be("Sig1");
        sut.SignalRows[1].Signal.Name.Should().Be("Sig2");
    }

    /// <summary>
    /// v1.4.0 MINOR: <c>SendCommand</c> encodes the per-signal
    /// <c>Value</c> entries into a CAN frame via
    /// <see cref="DbcEncodeService"/> and dispatches the frame through
    /// <see cref="SendService.SendAsync"/>. The frame ID comes from
    /// <c>SelectedDbcMessage.Id</c> and the DLC equals the message DLC.
    /// </summary>
    [Fact]
    public async Task SendAsync_EncodesValues_AndInvokesSendService()
    {
        var doc = MakeDoc(
            new Message(0x100u, "TestMsg", 8, "Test",
                new[] { MakeSignal("Sig1") },
                IsMultiplexed: false, MultiplexorSignalIndex: null));
        var dbcService = new DbcService(NullLogger<DbcService>.Instance);
        dbcService.SetCurrentForTests(doc);
        var sendService = new FakeSendService();
        var sut = new DbcSendViewModel(new DbcEncodeService(), sendService, dbcService);
        sut.SelectedDbcMessage = doc.Messages[0];
        sut.SignalRows[0].Value = 0x42;

        await sut.SendCommand.ExecuteAsync(null);

        sendService.LastFrame.Should().NotBeNull();
        var frame = sendService.LastFrame!.Value;
        frame.Id.Raw.Should().Be(0x100u);
        frame.Dlc.Should().Be(8);
        // Signal: start bit 0, length 8, little-endian, factor=1, value=0x42.
        // First byte should be 0x42.
        frame.Data.ToArray()[0].Should().Be(0x42);
    }
}
