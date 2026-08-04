using System.Threading.Channels;
using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.HIL;
using DbcValueType = PeakCan.HIL.Core.Dbc.ValueType;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests;

/// <summary>
/// Fake ICanChannel for testing HILAssertionContext without TraceDrivenChannel.
/// </summary>
internal sealed class FakeCanChannel : ICanChannel
{
    public ChannelId Id => new(1);
    public bool IsConnected { get; private set; }
    public event Action<CanFrame>? FrameReceived;
    public event Action<ReadLoopError>? ReadLoopError;

    public Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default)
    { IsConnected = true; return Task.FromResult(Result<Unit>.Ok(default)); }

    public Task DisconnectAsync(CancellationToken ct = default)
    { IsConnected = false; return Task.CompletedTask; }

    public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
        => ValueTask.FromResult(Result<Unit>.Ok(default));

    public ValueTask DisposeAsync() { IsConnected = false; return ValueTask.CompletedTask; }

    public void SimulateFrame(CanFrame frame) => FrameReceived?.Invoke(frame);

    // Satisfy CS0677: event is part of ICanChannel contract even if tests don't use it
    public void SimulateError(ReadLoopError error) => ReadLoopError?.Invoke(error);
}

/// <summary>
/// Fake IDbcLookup with configurable messages.
/// </summary>
internal sealed class FakeDbcLookup : IDbcLookup
{
    private readonly Dictionary<uint, Message> _messages = new();
    public void AddMessage(Message msg) => _messages[msg.Id] = msg;
    public Message? FindMessage(uint canId) =>
        _messages.TryGetValue(canId, out var msg) ? msg : null;
}

/// <summary>
/// TDD tests for HILAssertionContext (Sprint 2 Inc 2).
/// </summary>
public class HILAssertionContextTests
{
    private static Message CreateMessage(uint id, string name, params Signal[] signals)
        => new(id, name, 8, "TestSender", signals, false, null);

    private static Signal CreateSignal(string name, ushort startBit, byte length,
        ByteOrder order = default, DbcValueType valueType = DbcValueType.Unsigned,
        double factor = 1, double offset = 0)
        => new(name, startBit, length, order, valueType, factor, offset,
            0, 1000, "", Array.Empty<string>());

    [Fact]
    public void Constructor_subscribes_to_channel_FrameReceived()
    {
        var channel = new FakeCanChannel();
        var dbc = new FakeDbcLookup();
        using var ctx = new HILAssertionContext(channel, dbc);

        var callbackInvoked = false;
        using var sub = ctx.SubscribeDecodedFrames(_ => callbackInvoked = true);

        channel.SimulateFrame(new CanFrame(new CanId(0x100, FrameFormat.Standard),
            new byte[] { 0x01 }, FrameFlags.None, new ChannelId(1), new Timestamp(0)));

        // Give consumer thread time to process
        Thread.Sleep(200);
        callbackInvoked.Should().BeTrue("constructor should subscribe to channel.FrameReceived");
    }

    [Fact]
    public void OnFrame_writes_CanFrame_to_channel_without_blocking()
    {
        var channel = new FakeCanChannel();
        var dbc = new FakeDbcLookup();
        using var ctx = new HILAssertionContext(channel, dbc);

        var decodedFrames = new List<DecodedFrame>();
        using var sub = ctx.SubscribeDecodedFrames(f => decodedFrames.Add(f));

        channel.SimulateFrame(new CanFrame(new CanId(0x100, FrameFormat.Standard),
            new byte[] { 0x42 }, FrameFlags.None, new ChannelId(1), new Timestamp(0)));

        Thread.Sleep(200);
        decodedFrames.Should().HaveCount(1);
        decodedFrames[0].Frame.Id.Raw.Should().Be(0x100u);
    }

    [Fact]
    public void ConsumerLoop_decodes_signals_and_populates_signalCache()
    {
        var channel = new FakeCanChannel();
        var dbc = new FakeDbcLookup();
        // Message ID=0x100 (standard), signal "RPM" at startBit=0, length=8, unsigned, factor=1, offset=0
        var msg = CreateMessage(0x100, "BMS_Status", CreateSignal("RPM", 0, 8));
        dbc.AddMessage(msg);

        using var ctx = new HILAssertionContext(channel, dbc);

        // Frame with data[0]=0x64 (100 decimal) -> RPM=100
        channel.SimulateFrame(new CanFrame(new CanId(0x100, FrameFormat.Standard),
            new byte[] { 0x64 }, FrameFlags.None, new ChannelId(1), new Timestamp(0)));

        Thread.Sleep(200);
        var value = ctx.GetSignalValue("BMS_Status.RPM");
        value.Should().Be(100.0, "signal should be decoded from frame data");
    }

    [Fact]
    public void ConsumerLoop_extended_frame_DBC_lookup_uses_bit_31_key()
    {
        var channel = new FakeCanChannel();
        var dbc = new FakeDbcLookup();
        // Extended message: DBC stores ID with bit 31 set (0x98FEF100)
        var msg = CreateMessage(0x98FEF100, "ExtMsg", CreateSignal("Sig", 0, 8));
        dbc.AddMessage(msg);

        using var ctx = new HILAssertionContext(channel, dbc);

        // CanFrame uses raw ID without bit 31 (0x18FEF100)
        channel.SimulateFrame(new CanFrame(new CanId(0x18FEF100, FrameFormat.Extended),
            new byte[] { 0x42 }, FrameFlags.None, new ChannelId(1), new Timestamp(0)));

        Thread.Sleep(200);
        var value = ctx.GetSignalValue("ExtMsg.Sig");
        value.Should().Be(0x42, "extended frame should be looked up via ToDbcLookupKey conversion");
    }

    [Fact]
    public void ConsumerLoop_frame_not_in_DBC_emits_DecodedFrame_with_empty_signals()
    {
        var channel = new FakeCanChannel();
        var dbc = new FakeDbcLookup();
        using var ctx = new HILAssertionContext(channel, dbc);

        var decodedFrames = new List<DecodedFrame>();
        using var sub = ctx.SubscribeDecodedFrames(f => decodedFrames.Add(f));

        channel.SimulateFrame(new CanFrame(new CanId(0x999, FrameFormat.Extended),
            new byte[] { 0xFF }, FrameFlags.None, new ChannelId(1), new Timestamp(0)));

        Thread.Sleep(200);
        decodedFrames.Should().HaveCount(1);
        decodedFrames[0].Signals.Should().BeEmpty("unknown frame should have empty signals");
    }

    [Fact]
    public void ConsumerLoop_subscriber_callback_throws_isolated()
    {
        var channel = new FakeCanChannel();
        var dbc = new FakeDbcLookup();
        using var ctx = new HILAssertionContext(channel, dbc);

        var secondCallbackInvoked = false;

        // First subscriber throws
        ctx.SubscribeDecodedFrames(_ => throw new InvalidOperationException("test exception"));
        // Second subscriber records
        using var sub2 = ctx.SubscribeDecodedFrames(_ => secondCallbackInvoked = true);

        channel.SimulateFrame(new CanFrame(new CanId(0x100, FrameFormat.Standard),
            new byte[] { 0x01 }, FrameFlags.None, new ChannelId(1), new Timestamp(0)));

        Thread.Sleep(200);
        secondCallbackInvoked.Should().BeTrue("second subscriber should still receive callback despite first throwing");
    }

    [Fact]
    public void SubscribeDecodedFrames_returns_IDisposable_that_unsubscribes()
    {
        var channel = new FakeCanChannel();
        var dbc = new FakeDbcLookup();
        using var ctx = new HILAssertionContext(channel, dbc);

        var callbackCount = 0;
        var sub = ctx.SubscribeDecodedFrames(_ => Interlocked.Increment(ref callbackCount));

        channel.SimulateFrame(new CanFrame(new CanId(0x100, FrameFormat.Standard),
            new byte[] { 0x01 }, FrameFlags.None, new ChannelId(1), new Timestamp(0)));
        Thread.Sleep(200);
        callbackCount.Should().BeGreaterThan(0, "callback should fire before unsubscribe");

        sub.Dispose();

        var countAfterUnsubscribe = callbackCount;
        channel.SimulateFrame(new CanFrame(new CanId(0x100, FrameFormat.Standard),
            new byte[] { 0x02 }, FrameFlags.None, new ChannelId(1), new Timestamp(1)));
        Thread.Sleep(200);
        callbackCount.Should().Be(countAfterUnsubscribe, "callback should NOT fire after unsubscribe");
    }

    [Fact]
    public void SubscribeDecodedFrames_multiple_subscribers_all_notified()
    {
        var channel = new FakeCanChannel();
        var dbc = new FakeDbcLookup();
        using var ctx = new HILAssertionContext(channel, dbc);

        var count1 = 0;
        var count2 = 0;
        var count3 = 0;

        using var s1 = ctx.SubscribeDecodedFrames(_ => Interlocked.Increment(ref count1));
        using var s2 = ctx.SubscribeDecodedFrames(_ => Interlocked.Increment(ref count2));
        using var s3 = ctx.SubscribeDecodedFrames(_ => Interlocked.Increment(ref count3));

        channel.SimulateFrame(new CanFrame(new CanId(0x100, FrameFormat.Standard),
            new byte[] { 0x01 }, FrameFlags.None, new ChannelId(1), new Timestamp(0)));

        Thread.Sleep(200);
        count1.Should().Be(1);
        count2.Should().Be(1);
        count3.Should().Be(1);
    }

    [Fact]
    public void GetSignalValue_signal_not_found_returns_null()
    {
        var channel = new FakeCanChannel();
        var dbc = new FakeDbcLookup();
        using var ctx = new HILAssertionContext(channel, dbc);

        var value = ctx.GetSignalValue("Nonexistent.Signal");
        value.Should().BeNull();
    }

    [Fact]
    public void GetSignalValue_fresh_signal_returns_value()
    {
        var channel = new FakeCanChannel();
        var dbc = new FakeDbcLookup();
        var msg = CreateMessage(0x100, "BMS_Status", CreateSignal("RPM", 0, 8));
        dbc.AddMessage(msg);

        using var ctx = new HILAssertionContext(channel, dbc);

        channel.SimulateFrame(new CanFrame(new CanId(0x100, FrameFormat.Standard),
            new byte[] { 0x64 }, FrameFlags.None, new ChannelId(1), new Timestamp(1_000_000)));

        Thread.Sleep(200);
        var value = ctx.GetSignalValue("BMS_Status.RPM");
        value.Should().Be(100.0);
    }

    [Fact]
    public void GetSignalValue_stale_signal_returns_null()
    {
        var channel = new FakeCanChannel();
        var dbc = new FakeDbcLookup();
        var msg = CreateMessage(0x100, "BMS_Status", CreateSignal("RPM", 0, 8));
        dbc.AddMessage(msg);

        using var ctx = new HILAssertionContext(channel, dbc);

        // Frame at t=1s
        channel.SimulateFrame(new CanFrame(new CanId(0x100, FrameFormat.Standard),
            new byte[] { 0x64 }, FrameFlags.None, new ChannelId(1), new Timestamp(1_000_000)));
        Thread.Sleep(200);

        // Advance current timestamp to t=7s (via a later frame on a different ID)
        channel.SimulateFrame(new CanFrame(new CanId(0x200, FrameFormat.Standard),
            new byte[] { 0x00 }, FrameFlags.None, new ChannelId(1), new Timestamp(7_000_000)));
        Thread.Sleep(200);

        // RPM was decoded at t=1s, now current is t=7s, age=6s > maxAgeMs=5000
        var value = ctx.GetSignalValue("BMS_Status.RPM", maxAgeMs: 5000);
        value.Should().BeNull("signal older than maxAgeMs should return null");
    }

    [Fact]
    public void GetSignalValue_maxAgeMs_zero_disables_staleness_check()
    {
        var channel = new FakeCanChannel();
        var dbc = new FakeDbcLookup();
        var msg = CreateMessage(0x100, "BMS_Status", CreateSignal("RPM", 0, 8));
        dbc.AddMessage(msg);

        using var ctx = new HILAssertionContext(channel, dbc);

        channel.SimulateFrame(new CanFrame(new CanId(0x100, FrameFormat.Standard),
            new byte[] { 0x64 }, FrameFlags.None, new ChannelId(1), new Timestamp(1_000_000)));
        Thread.Sleep(200);

        // Advance to t=100s
        channel.SimulateFrame(new CanFrame(new CanId(0x200, FrameFormat.Standard),
            new byte[] { 0x00 }, FrameFlags.None, new ChannelId(1), new Timestamp(100_000_000)));
        Thread.Sleep(200);

        // maxAgeMs=0 disables staleness check
        var value = ctx.GetSignalValue("BMS_Status.RPM", maxAgeMs: 0);
        value.Should().Be(100.0, "maxAgeMs=0 should disable staleness check");
    }

    [Fact]
    public void CurrentTimestamp_updates_on_each_frame()
    {
        var channel = new FakeCanChannel();
        var dbc = new FakeDbcLookup();
        using var ctx = new HILAssertionContext(channel, dbc);

        channel.SimulateFrame(new CanFrame(new CanId(0x100, FrameFormat.Standard),
            new byte[] { 0x01 }, FrameFlags.None, new ChannelId(1), new Timestamp(1_000_000)));
        Thread.Sleep(200);
        ctx.CurrentTimestamp.Should().Be(1_000_000, "timestamp should update after first frame");

        channel.SimulateFrame(new CanFrame(new CanId(0x200, FrameFormat.Standard),
            new byte[] { 0x02 }, FrameFlags.None, new ChannelId(1), new Timestamp(2_000_000)));
        Thread.Sleep(200);
        ctx.CurrentTimestamp.Should().Be(2_000_000, "timestamp should update after second frame");
    }

    [Fact]
    public async Task SendFrameAsync_returns_success()
    {
        var channel = new FakeCanChannel();
        var dbc = new FakeDbcLookup();
        using var ctx = new HILAssertionContext(channel, dbc);

        var frame = new CanFrame(new CanId(0x100, FrameFormat.Standard),
            new byte[] { 0x01 }, FrameFlags.None, new ChannelId(1), new Timestamp(0));

        var result = await ctx.SendFrameAsync(frame);
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Dispose_unsubscribes_from_channel()
    {
        var channel = new FakeCanChannel();
        var dbc = new FakeDbcLookup();
        var ctx = new HILAssertionContext(channel, dbc);

        var callbackCount = 0;
        using (ctx.SubscribeDecodedFrames(_ => Interlocked.Increment(ref callbackCount)))
        {
            channel.SimulateFrame(new CanFrame(new CanId(0x100, FrameFormat.Standard),
                new byte[] { 0x01 }, FrameFlags.None, new ChannelId(1), new Timestamp(0)));
            Thread.Sleep(200);
            callbackCount.Should().BeGreaterThan(0);
        }

        ctx.Dispose();

        var countAfterDispose = callbackCount;
        channel.SimulateFrame(new CanFrame(new CanId(0x100, FrameFormat.Standard),
            new byte[] { 0x02 }, FrameFlags.None, new ChannelId(1), new Timestamp(1)));
        Thread.Sleep(200);
        callbackCount.Should().Be(countAfterDispose, "no callbacks after Dispose");
    }

    [Fact]
    public void Dispose_drains_channel_and_cancels_consumer()
    {
        var channel = new FakeCanChannel();
        var dbc = new FakeDbcLookup();
        var ctx = new HILAssertionContext(channel, dbc);

        // Queue a frame
        channel.SimulateFrame(new CanFrame(new CanId(0x100, FrameFormat.Standard),
            new byte[] { 0x01 }, FrameFlags.None, new ChannelId(1), new Timestamp(0)));

        // Dispose should complete within 3s
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ctx.Dispose();
        sw.ElapsedMilliseconds.Should().BeLessThan(3000, "Dispose should complete within 3s");
    }
}
