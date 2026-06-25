using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Tests.Collections;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;
using DbcValueType = PeakCan.Host.Core.Dbc.ValueType;
using Xunit;

namespace PeakCan.Host.App.Tests.Services;

/// <summary>
/// Verifies that the DBC decode path runs OFF the SDK read thread.
/// The contract: <see cref="DbcDecodeBackgroundService.OnFrame"/> must
/// return quickly (no DBC lookup, no SignalEntry construction). The
/// decoded-signal observable in <see cref="SignalViewModel.Latest"/> is
/// updated asynchronously on the service's own worker.
/// <para>
/// <b>v1.2.1 PATCH (Task 6):</b> the ctor calls
/// <see cref="LeakedApplicationReset.CleanupLeakedApplication"/> to null
/// out any leaked <see cref="System.Windows.Application.Current"/> from a
/// sibling test class (xUnit runs test classes in parallel). Without this,
/// the inline path inside <see cref="SignalViewModel.ApplyFrame"/> would
/// route through <c>Dispatcher.InvokeAsync</c> on a dead dispatcher and
/// <see cref="SignalViewModel.Latest"/> would stay empty — same root cause
/// as the v1.2.0 §9.1 SignalViewModel flake closed in Task 5
/// (commit <c>23e7d7c</c>).
/// </para>
/// </summary>
public class DbcDecodeBackgroundServiceTests
{
    // v1.2.1 PATCH Task 6: defensive cleanup of leaked Application.Current
    // before each test (ctor runs once per test instance in xUnit).
    public DbcDecodeBackgroundServiceTests() => LeakedApplicationReset.CleanupLeakedApplication();

    /// <summary>
    /// Build a one-signal DBC matching id 0x100. Uses the actual record
    /// signatures (5-arg <see cref="DbcDocument"/>, 11-arg <see cref="Signal"/>,
    /// 5-arg <see cref="Message"/>) — the brief's helper used shorthand
    /// positional args that no longer compile against the current Core types.
    /// </summary>
    private static DbcDocument DocWithOneSignal()
    {
        var sig = new Signal(
            Name: "S1", StartBit: 0, Length: 8,
            Order: ByteOrder.LittleEndian,
            ValueType: DbcValueType.Unsigned,
            Factor: 1.0, Offset: 0.0,
            Min: 0, Max: 255, Unit: "u", Receivers: Array.Empty<string>());
        var msg = new Message(
            Id: 0x100, Name: "M1", Dlc: 8, Sender: "n1",
            Signals: new[] { sig },
            IsMultiplexed: false, MultiplexorSignalIndex: null);
        var dict = new Dictionary<uint, Message> { [0x100] = msg };
        return new DbcDocument(
            Version: "",
            Nodes: Array.Empty<Node>(),
            Messages: new[] { msg },
            MessagesById: dict,
            ValueTables: new Dictionary<string, ValueTable>());
    }

    [Fact]
    public async Task OnFrame_With_Matching_Dbc_Decodes_Eventually()
    {
        var dbc = new DbcService(NullLogger<DbcService>.Instance);
        dbc.SetCurrentForTests(DocWithOneSignal());
        var sigVm = new SignalViewModel();
        var svc = new DbcDecodeBackgroundService(dbc, sigVm);

        using var startCts = new System.Threading.CancellationTokenSource();
        await svc.StartAsync(startCts.Token);
        try
        {
            var frame = new CanFrame(
                new CanId(0x100, FrameFormat.Standard),
                new byte[] { 0x42 },
                FrameFlags.None,
                new ChannelId(0x51),
                Timestamp.FromMicroseconds(0));
            svc.OnFrame(frame);

            // Wait up to 5 s for the service to drain. CI Windows runners
            // under load can take several seconds for the BackgroundService
            // worker to dequeue and decode; 1 s is too aggressive and flakes
            // the PR build (pre-existing on main).
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (sigVm.Latest.Count == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            sigVm.Latest.Should().HaveCount(1, "the matching DBC message must produce one decoded-signal row");
            sigVm.Latest[0].Signal.Should().Be("S1");
            sigVm.Latest[0].Message.Should().Be("M1");
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task OnFrame_Without_Dbc_Loaded_Is_NoOp()
    {
        var dbc = new DbcService(NullLogger<DbcService>.Instance);
        var sigVm = new SignalViewModel();
        var svc = new DbcDecodeBackgroundService(dbc, sigVm);

        using var startCts = new System.Threading.CancellationTokenSource();
        await svc.StartAsync(startCts.Token);
        try
        {
            var frame = new CanFrame(
                new CanId(0x100, FrameFormat.Standard),
                new byte[] { 0x42 },
                FrameFlags.None,
                new ChannelId(0x51),
                Timestamp.FromMicroseconds(0));
            svc.OnFrame(frame); // must not throw, must not enqueue work that loops forever

            await Task.Delay(50);
            sigVm.Latest.Should().BeEmpty();
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }
    }

}
