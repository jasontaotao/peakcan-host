using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
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
        var traceVm = new TraceViewModel();
        var svc = new DbcDecodeBackgroundService(dbc, sigVm, traceVm, NullLogger<DbcDecodeBackgroundService>.Instance);

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
        var traceVm = new TraceViewModel();
        var svc = new DbcDecodeBackgroundService(dbc, sigVm, traceVm, NullLogger<DbcDecodeBackgroundService>.Instance);

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

    /// <summary>
    /// Build a one-signal DBC matching an Extended message id with the
    /// merged-IDE bit set (bit 31), matching PEAK's
    /// <c>PCAN_MESSAGE_ID</c> convention used by <see cref="DbcParser"/>
    /// and recorded at <see cref="Message.Id"/>.
    /// </summary>
    private static DbcDocument DocWithOneExtendedSignal()
    {
        var sig = new Signal(
            Name: "Ext1", StartBit: 0, Length: 8,
            Order: ByteOrder.LittleEndian,
            ValueType: DbcValueType.Unsigned,
            Factor: 1.0, Offset: 0.0,
            Min: 0, Max: 255, Unit: "u", Receivers: Array.Empty<string>());
        // Merged-IDE form: bit 31 set for Extended (matches PCAN_MESSAGE_ID).
        var mergedId = 0x80000100u;
        var msg = new Message(
            Id: mergedId, Name: "MExt", Dlc: 8, Sender: "n1",
            Signals: new[] { sig },
            IsMultiplexed: false, MultiplexorSignalIndex: null);
        var dict = new Dictionary<uint, Message> { [mergedId] = msg };
        return new DbcDocument(
            Version: "",
            Nodes: Array.Empty<Node>(),
            Messages: new[] { msg },
            MessagesById: dict,
            ValueTables: new Dictionary<string, ValueTable>());
    }

    [Fact]
    public async Task OnFrame_With_Extended_Id_Decodes_When_Dbc_Has_Merged_Ide_Bit()
    {
        // Regression test for the v1.2.4 PATCH extended-frame decode bug:
        // PEAK's CanId.Raw carries 11/29-bit pure IDs (bit 31 = 0), but
        // DbcDocument.MessagesById is keyed by the merged-IDE convention
        // (bit 31 set for Extended, matching PCAN_MESSAGE_ID and
        // DbcParser's BO_ 2147487744 = 0x80001000 acceptance). Without
        // normalizing frame.Id.Raw to the merged-IDE form before lookup,
        // every Extended frame misses the dictionary and SignalViewModel
        // stays empty for any DBC that contains an Extended message.
        var dbc = new DbcService(NullLogger<DbcService>.Instance);
        dbc.SetCurrentForTests(DocWithOneExtendedSignal());
        var sigVm = new SignalViewModel();
        var traceVm = new TraceViewModel();
        var svc = new DbcDecodeBackgroundService(dbc, sigVm, traceVm, NullLogger<DbcDecodeBackgroundService>.Instance);

        using var startCts = new System.Threading.CancellationTokenSource();
        await svc.StartAsync(startCts.Token);
        try
        {
            // Pure 29-bit ID in Extended format — bit 31 must be 0 per
            // CanId ctor validation (>0x1FFFFFFF throws).
            var frame = new CanFrame(
                new CanId(0x100, FrameFormat.Extended),
                new byte[] { 0x42 },
                FrameFlags.None,
                new ChannelId(0x51),
                Timestamp.FromMicroseconds(0));
            svc.OnFrame(frame);

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (sigVm.Latest.Count == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            sigVm.Latest.Should().HaveCount(1, "the matching DBC Extended message must produce one decoded-signal row");
            sigVm.Latest[0].Signal.Should().Be("Ext1");
            sigVm.Latest[0].Message.Should().Be("MExt");
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }
    }

    // --- v1.2.11 PATCH Item 2: DbcDecodeBackgroundService fills TraceEntry.Decoded ---

    [Fact]
    public async Task Worker_Fills_Decoded_Of_Pending_TraceEntry()
    {
        // v1.2.11: when a frame matches DBC AND a pending TraceEntry exists
        // with the same (IdRaw, TimestampMicroseconds, ChannelHandle), the
        // worker must fill entry.Decoded with a "Name=Value, ..." string.
        var dbc = new DbcService(NullLogger<DbcService>.Instance);
        dbc.SetCurrentForTests(DocWithOneSignal());
        var sigVm = new SignalViewModel();
        var traceVm = new TraceViewModel();
        var entry = new TraceEntry
        {
            Id = new CanId(0x100, FrameFormat.Standard),
            Timestamp = Timestamp.FromMicroseconds(0UL),
            Channel = new ChannelId(0x51),
            DataHex = "42",
        };
        // Inject pending entry directly — RegisterForTesting bypasses the
        // AppendBatchAsync dispatcher path that no-ops without WPF Application.
        traceVm.RegisterForTesting(
            new TraceEntryKey(0x100, 0UL, 0x51), entry);

        var svc = new DbcDecodeBackgroundService(dbc, sigVm, traceVm, NullLogger<DbcDecodeBackgroundService>.Instance);

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

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (entry.Decoded == "" && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            entry.Decoded.Should().NotBeEmpty("the matching DBC frame must fill the pending entry's Decoded");
            entry.Decoded.Should().Contain("S1", "the decoded string should name the signal");
            entry.Decoded.Should().Contain("66", "0x42 = 66 unsigned with factor=1.0");
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Worker_NoOp_When_Frame_Not_In_Dbc()
    {
        // v1.2.11: if frame id is not in DBC, the worker skips the lookup
        // entirely and pending entry's Decoded stays "".
        var dbc = new DbcService(NullLogger<DbcService>.Instance);
        dbc.SetCurrentForTests(DocWithOneSignal());  // only id 0x100
        var sigVm = new SignalViewModel();
        var traceVm = new TraceViewModel();
        var entry = new TraceEntry
        {
            Id = new CanId(0x555, FrameFormat.Standard),
            Timestamp = Timestamp.FromMicroseconds(0UL),
            Channel = new ChannelId(0x51),
            DataHex = "42",
        };
        traceVm.RegisterForTesting(
            new TraceEntryKey(0x555, 0UL, 0x51), entry);

        var svc = new DbcDecodeBackgroundService(dbc, sigVm, traceVm, NullLogger<DbcDecodeBackgroundService>.Instance);

        using var startCts = new System.Threading.CancellationTokenSource();
        await svc.StartAsync(startCts.Token);
        try
        {
            var frame = new CanFrame(
                new CanId(0x555, FrameFormat.Standard),
                new byte[] { 0x42 },
                FrameFlags.None,
                new ChannelId(0x51),
                Timestamp.FromMicroseconds(0));
            svc.OnFrame(frame);

            await Task.Delay(200);   // give worker time to process (and skip)

            entry.Decoded.Should().Be("", "frames not in DBC must not touch Decoded");
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Worker_NoThrow_When_Pending_Missing()
    {
        // v1.2.11: if frame is in DBC but no pending entry exists, the worker
        // must not throw — SignalViewModel still receives the decoded signal.
        var dbc = new DbcService(NullLogger<DbcService>.Instance);
        dbc.SetCurrentForTests(DocWithOneSignal());
        var sigVm = new SignalViewModel();
        var traceVm = new TraceViewModel();   // empty — no pending entries

        var svc = new DbcDecodeBackgroundService(dbc, sigVm, traceVm, NullLogger<DbcDecodeBackgroundService>.Instance);

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

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (sigVm.Latest.Count == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            sigVm.Latest.Should().HaveCount(1, "SignalViewModel still receives the decoded signal");
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }
    }

    // ===== v1.2.12 PATCH Item 11: sink OnError → ILogger =====

    [Fact]
    public void OnError_Calls_ILogger_LogWarning()
    {
        // Source-gen [LoggerMessage] gates Log() on IsEnabled, so stub true.
        var logger = Substitute.For<ILogger<DbcDecodeBackgroundService>>();
        logger.IsEnabled(LogLevel.Warning).Returns(true);
        var dbc = new DbcService(NullLogger<DbcService>.Instance);
        var sigVm = new SignalViewModel();
        var traceVm = new TraceViewModel();
        var svc = new DbcDecodeBackgroundService(dbc, sigVm, traceVm, logger);
        var ex = new InvalidOperationException("test");

        svc.OnError(ex);

        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            ex,
            Arg.Any<Func<object, Exception?, string>>());
    }
}
