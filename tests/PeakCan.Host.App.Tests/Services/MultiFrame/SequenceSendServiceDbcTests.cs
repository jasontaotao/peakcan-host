using FluentAssertions;
using PeakCan.Host.App.Models;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.MultiFrame;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Dbc;
using PeakCan.Host.Infrastructure.Channel;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.MultiFrame;

/// <summary>
/// v2.1.1 PATCH tests for <see cref="SequenceSendService"/> —
/// DBC-row encoding via <see cref="DbcEncodeService"/>: row
/// kind dispatch, signal value translation, mixed raw+DBC
/// sequences, missing-message handling, and per-row failure
/// isolation.
/// </summary>
public sealed class SequenceSendServiceDbcTests
{
    /// <summary>Recording ICanChannel — records written frames.</summary>
    private sealed class RecordingChannel : ICanChannel
    {
        public ChannelId Id { get; }
        public bool IsConnected { get; private set; }
        public List<CanFrame> Written { get; } = new();
        public RecordingChannel(ChannelId id) { Id = id; IsConnected = true; }
#pragma warning disable CS0067
        public event Action<CanFrame>? FrameReceived;
        // v3.16.9.4 PATCH: ICanChannel gained ReadLoopError event — unused
        // in this test fake, but must exist to satisfy the interface.
#pragma warning disable CS0067
        public event Action<ReadLoopError>? ReadLoopError;
#pragma warning restore CS0067
#pragma warning restore CS0067
        public Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default)
        { IsConnected = true; return Task.FromResult(Result<Unit>.Ok(default)); }
        public Task DisconnectAsync(CancellationToken ct = default)
        { IsConnected = false; return Task.CompletedTask; }
        public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
        { Written.Add(frame); return ValueTask.FromResult(Result<Unit>.Ok(default)); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Stub DbcEncodeService: returns a fixed payload, no real encoding.</summary>
    private sealed class StubDbcEncoder : DbcEncodeService
    {
        public List<(Message Msg, IReadOnlyDictionary<string, double> Vals)> Calls { get; } = new();
        public byte[] NextPayload { get; set; } = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 };
        public override byte[] Encode(Message message, IReadOnlyDictionary<string, double> signalValues)
        {
            Calls.Add((message, signalValues));
            return NextPayload;
        }
    }

    private static SendService NewSendService(RecordingChannel ch)
    {
        var svc = new SendService(Microsoft.Extensions.Logging.Abstractions.NullLogger<SendService>.Instance);
        svc.ActiveChannel = ch;
        return svc;
    }

    private static DbcDocument MakeDoc(params Message[] messages) =>
        new DbcDocument(
            Version: "v1",
            Nodes: Array.Empty<Node>(),
            Messages: messages,
            MessagesById: messages.ToDictionary(m => m.Id),
            ValueTables: new Dictionary<string, ValueTable>());

    private static Message MakeMessage(uint id, string name, params Signal[] signals) =>
        new Message(id, name, Dlc: 8, Sender: "ECU",
            Signals: signals, IsMultiplexed: false, MultiplexorSignalIndex: null);

    private static Signal MakeSignal(string name, byte startBit = 0, byte length = 8) =>
        new Signal(name, startBit, length,
            ByteOrder.LittleEndian, PeakCan.Host.Core.Dbc.ValueType.Unsigned,
            Factor: 1.0, Offset: 0.0,
            Min: 0, Max: 100, Unit: "", Receivers: Array.Empty<string>());

    [Fact]
    public async Task SendAsync_DbcRow_EncodesViaDbcEncodeService()
    {
        var ch = new RecordingChannel(ChannelId.None);
        var msg = MakeMessage(0x100, "EngineRPM");
        var doc = MakeDoc(msg);
        var stubSvc = new DbcService(Microsoft.Extensions.Logging.Abstractions.NullLogger<DbcService>.Instance);
        stubSvc.SetCurrentForTests(doc);
        var enc = new StubDbcEncoder();
        var seqSvc = new SequenceSendService(NewSendService(ch), enc, stubSvc);

        var row = new MultiFrameSequenceRow
        {
            RowKind = MultiFrameSequenceRow.Kind.Dbc,
            DbcMessageName = "EngineRPM",
        };
        row.DbcSignalValues.Add(new DbcSignalValue { Name = "rpm", Value = 2500 });

        var r = await seqSvc.SendAsync(new[] { row }, SequenceSendService.Mode.Concurrent, 0, 1);

        r.SentCount.Should().Be(1);
        r.FailureCount.Should().Be(0);
        enc.Calls.Should().HaveCount(1, "DBC row must invoke DbcEncodeService exactly once");
        enc.Calls[0].Msg.Name.Should().Be("EngineRPM");
        enc.Calls[0].Vals.Should().ContainKey("rpm").WhoseValue.Should().Be(2500);
        ch.Written.Should().HaveCount(1);
        ch.Written[0].Id.Raw.Should().Be(0x100u);
        ch.Written[0].Data.Should().Equal(0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88);
    }

    [Fact]
    public async Task SendAsync_DbcRow_ExtendedId_PreservesIdeBit()
    {
        // PEAK convention: bit 31 set → Extended 29-bit ID.
        var ch = new RecordingChannel(ChannelId.None);
        var msg = MakeMessage(0x80000100u, "ExtMsg"); // Extended
        var doc = MakeDoc(msg);
        var stubSvc = new DbcService(Microsoft.Extensions.Logging.Abstractions.NullLogger<DbcService>.Instance);
        stubSvc.SetCurrentForTests(doc);
        var enc = new StubDbcEncoder();
        var seqSvc = new SequenceSendService(NewSendService(ch), enc, stubSvc);

        var row = new MultiFrameSequenceRow { RowKind = MultiFrameSequenceRow.Kind.Dbc, DbcMessageName = "ExtMsg" };
        await seqSvc.SendAsync(new[] { row }, SequenceSendService.Mode.Concurrent, 0, 1);

        // Frame format must be Extended (bit 31 stripped from raw).
        ch.Written[0].Id.Format.Should().Be(FrameFormat.Extended);
        ch.Written[0].Id.Raw.Should().Be(0x100u, "IDE bit must be stripped from the on-wire raw value");
    }

    [Fact]
    public async Task SendAsync_MixedRawAndDbcRows_AllBuiltAndDispatched()
    {
        var ch = new RecordingChannel(ChannelId.None);
        var msg = MakeMessage(0x100, "Foo");
        var doc = MakeDoc(msg);
        var stubSvc = new DbcService(Microsoft.Extensions.Logging.Abstractions.NullLogger<DbcService>.Instance);
        stubSvc.SetCurrentForTests(doc);
        var enc = new StubDbcEncoder();
        var seqSvc = new SequenceSendService(NewSendService(ch), enc, stubSvc);

        var rawRow = new MultiFrameSequenceRow { RowKind = MultiFrameSequenceRow.Kind.Raw, Id = 0x200, DataHex = "AABB" };
        var dbcRow = new MultiFrameSequenceRow { RowKind = MultiFrameSequenceRow.Kind.Dbc, DbcMessageName = "Foo" };

        var r = await seqSvc.SendAsync(new[] { rawRow, dbcRow },
            SequenceSendService.Mode.Sequential, 0, 1);

        r.SentCount.Should().Be(2);
        enc.Calls.Should().HaveCount(1, "only the DBC row invokes DbcEncodeService");
        ch.Written.Should().HaveCount(2);
        ch.Written[0].Id.Raw.Should().Be(0x200u, "raw row goes first (sequential order)");
        ch.Written[1].Id.Raw.Should().Be(0x100u, "DBC row goes second");
    }

    [Fact]
    public async Task SendAsync_DbcRow_NoDbcLoaded_CountsAsRowFailure()
    {
        var ch = new RecordingChannel(ChannelId.None);
        var stubSvc = new DbcService(Microsoft.Extensions.Logging.Abstractions.NullLogger<DbcService>.Instance);
        // No SetCurrentForTests → Current is null
        var enc = new StubDbcEncoder();
        var seqSvc = new SequenceSendService(NewSendService(ch), enc, stubSvc);

        var row = new MultiFrameSequenceRow { RowKind = MultiFrameSequenceRow.Kind.Dbc, DbcMessageName = "X" };
        var r = await seqSvc.SendAsync(new[] { row }, SequenceSendService.Mode.Concurrent, 0, 1);

        r.SentCount.Should().Be(0);
        r.FailureCount.Should().Be(1, "missing DBC doc → row fails, no abort of sequence");
        ch.Written.Should().BeEmpty();
    }

    [Fact]
    public async Task SendAsync_DbcRow_UnknownMessage_CountsAsRowFailure()
    {
        var ch = new RecordingChannel(ChannelId.None);
        var doc = MakeDoc(MakeMessage(0x100, "KnownMsg"));
        var stubSvc = new DbcService(Microsoft.Extensions.Logging.Abstractions.NullLogger<DbcService>.Instance);
        stubSvc.SetCurrentForTests(doc);
        var enc = new StubDbcEncoder();
        var seqSvc = new SequenceSendService(NewSendService(ch), enc, stubSvc);

        var row = new MultiFrameSequenceRow
        {
            RowKind = MultiFrameSequenceRow.Kind.Dbc,
            DbcMessageName = "NotInDoc",
        };
        var r = await seqSvc.SendAsync(new[] { row }, SequenceSendService.Mode.Concurrent, 0, 1);

        r.FailureCount.Should().Be(1);
        enc.Calls.Should().BeEmpty("encoder must not be called when message name doesn't match");
    }

    [Fact]
    public async Task SendAsync_DbcRow_EncoderThrows_CountsAsRowFailure()
    {
        var ch = new RecordingChannel(ChannelId.None);
        var msg = MakeMessage(0x100, "Boom");
        var doc = MakeDoc(msg);
        var stubSvc = new DbcService(Microsoft.Extensions.Logging.Abstractions.NullLogger<DbcService>.Instance);
        stubSvc.SetCurrentForTests(doc);

        // Throwing encoder
        var throwingEnc = new ThrowingDbcEncoder();
        var seqSvc = new SequenceSendService(NewSendService(ch), throwingEnc, stubSvc);

        var row = new MultiFrameSequenceRow
        {
            RowKind = MultiFrameSequenceRow.Kind.Dbc,
            DbcMessageName = "Boom",
        };
        var r = await seqSvc.SendAsync(new[] { row }, SequenceSendService.Mode.Concurrent, 0, 1);

        r.FailureCount.Should().Be(1);
        r.SentCount.Should().Be(0);
        ch.Written.Should().BeEmpty();
    }

    [Fact]
    public async Task SendAsync_DbcRow_EmptySignalValues_EncodesWithEmptyDict()
    {
        var ch = new RecordingChannel(ChannelId.None);
        var msg = MakeMessage(0x100, "X");
        var doc = MakeDoc(msg);
        var stubSvc = new DbcService(Microsoft.Extensions.Logging.Abstractions.NullLogger<DbcService>.Instance);
        stubSvc.SetCurrentForTests(doc);
        var enc = new StubDbcEncoder();
        var seqSvc = new SequenceSendService(NewSendService(ch), enc, stubSvc);

        var row = new MultiFrameSequenceRow { RowKind = MultiFrameSequenceRow.Kind.Dbc, DbcMessageName = "X" };
        // No signal values added → empty dict passed to encoder
        var r = await seqSvc.SendAsync(new[] { row }, SequenceSendService.Mode.Concurrent, 0, 1);

        r.SentCount.Should().Be(1);
        enc.Calls[0].Vals.Should().BeEmpty();
    }

    private sealed class ThrowingDbcEncoder : DbcEncodeService
    {
        public override byte[] Encode(Message message, IReadOnlyDictionary<string, double> signalValues)
            => throw new InvalidOperationException("encoder exploded");
    }
}