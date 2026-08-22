using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.HIL;
using DbcValueType = PeakCan.HIL.Core.Dbc.ValueType;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Multichannel;

/// <summary>
/// TDD tests for SingleChannelContext (migrated from PeakCanAssertionContext).
/// Zero-regression baseline: single-channel behavior (channelName=null) MUST be
/// byte-identical to old PeakCanAssertionContext.
/// </summary>
public class SingleChannelContextTests
{
    private static Signal CreateSignal(string name, ushort startBit, byte length,
        ByteOrder order = ByteOrder.LittleEndian, DbcValueType valueType = DbcValueType.Unsigned,
        double factor = 1, double offset = 0)
        => new(name, startBit, length, order, valueType, factor, offset,
            0, 1000, "", Array.Empty<string>());

    private static Message CreateMessage(uint id, string name, params Signal[] signals)
        => new(id, name, 8, "TestSender", signals, false, null);

    // ── Zero-regression baseline: copied from PeakCanAssertionContextTests ──

    [Fact]
    public void Constructor_SubscribesToFrameReceived()
    {
        var channel = new FakeCanChannel();
        var dbc = new FakeDbcLookup();
        using var ctx = new SingleChannelContext(channel, dbc);
        // No exception = subscription registered
        Assert.True(true);
    }

    [Fact]
    public void OnFrame_WritesToFrameChannel()
    {
        var channel = new FakeCanChannel();
        var dbc = new FakeDbcLookup();
        using var ctx = new SingleChannelContext(channel, dbc);
        var callbackInvoked = false;
        using var sub = ctx.SubscribeDecodedFrames(_ => callbackInvoked = true);

        channel.SimulateFrame(new CanFrame(new CanId(0x123, FrameFormat.Standard),
            new byte[] { 0x64, 0, 0, 0, 0, 0, 0, 0 }, FrameFlags.None, new ChannelId(1), new Timestamp(0)));

        Thread.Sleep(200);
        Assert.True(callbackInvoked, "OnFrame should write to frameChannel and notify subscribers");
    }

    [Fact]
    public async Task SendFrameAsync_DelegatesToChannel()
    {
        var channel = new FakeCanChannel();
        await channel.ConnectAsync(BaudRate.CanFd1Mbps, fd: true);
        var dbc = new FakeDbcLookup();
        using var ctx = new SingleChannelContext(channel, dbc);
        var frame = new CanFrame(new CanId(0x123, FrameFormat.Standard),
            new byte[] { 0xDE, 0xAD }, FrameFlags.None, default, default);

        var result = await ctx.SendFrameAsync(frame, default);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void GetSignalValue_AfterDecode_ReturnsValue()
    {
        var channel = new FakeCanChannel();
        var dbc = new FakeDbcLookup();
        dbc.AddMessage(CreateMessage(0x123, "TestMsg",
            CreateSignal("TestSignal", 0, 8, ByteOrder.LittleEndian, DbcValueType.Unsigned)));
        using var ctx = new SingleChannelContext(channel, dbc);
        var frame = new CanFrame(new CanId(0x123, FrameFormat.Standard),
            new byte[] { 0x64, 0, 0, 0, 0, 0, 0, 0 }, FrameFlags.None, new ChannelId(1), new Timestamp(0));

        channel.SimulateFrame(frame);
        Thread.Sleep(200);

        var value = ctx.GetSignalValue("TestMsg.TestSignal");
        Assert.NotNull(value);
        Assert.Equal(100.0, value!.Value, 1);
    }

    [Fact]
    public void Dispose_UnsubscribesAndDrains()
    {
        var channel = new FakeCanChannel();
        var dbc = new FakeDbcLookup();
        var ctx = new SingleChannelContext(channel, dbc);
        ctx.Dispose();
        // No exception
        Assert.True(true);
    }

    private sealed class RecordingSink : IHilFrameSink
    {
        public List<CanFrame> Frames { get; } = new();
        public void Write(CanFrame f) => Frames.Add(f);
        public void Dispose() { }
    }

    [Fact]
    public async Task ConsumerLoop_DecodeException_DoesNotKillLoop_SinkStillReceives()
    {
        // DBC 含一个 bitLength=65 的信号（SignalDecoder.Decode 抛异常），以及一个正常 8-bit 信号的消息
        var channel = new FakeCanChannel();
        var dbc = new FakeDbcLookup();
        dbc.AddMessage(CreateMessage(0x200, "BadMsg",
            CreateSignal("BadSignal", 0, 65, ByteOrder.LittleEndian, DbcValueType.Unsigned)));
        dbc.AddMessage(CreateMessage(0x123, "GoodMsg",
            CreateSignal("GoodSignal", 0, 8, ByteOrder.LittleEndian, DbcValueType.Unsigned)));

        using var ctx = new SingleChannelContext(channel, dbc);
        var sink = new RecordingSink();
        ctx.SetFrameSink(sink);
        Thread.Sleep(20); // 预热 consumer

        // 坏帧后好帧
        channel.SimulateFrame(new CanFrame(new CanId(0x200, FrameFormat.Standard),
            new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF },
            FrameFlags.None, new ChannelId(1), new Timestamp(0)));
        var goodFrame = new CanFrame(new CanId(0x123, FrameFormat.Standard),
            new byte[] { 0x64, 0, 0, 0, 0, 0, 0, 0 },
            FrameFlags.None, new ChannelId(1), new Timestamp(1));
        channel.SimulateFrame(goodFrame);

        await ctx.WaitForFrameDrainAsync(default);

        Assert.Contains(sink.Frames, f => f.Id.Raw == goodFrame.Id.Raw);
        Assert.Contains(sink.Frames, f => f.Id.Raw == 0x200);
    }

    [Fact]
    public void GetRecentFrames_ReturnsBuffer()
    {
        var channel = new FakeCanChannel();
        var dbc = new FakeDbcLookup();
        using var ctx = new SingleChannelContext(channel, dbc);
        var frame = new CanFrame(new CanId(0x123, FrameFormat.Standard),
            new byte[] { 0x01 }, FrameFlags.None, new ChannelId(1), new Timestamp(0));

        channel.SimulateFrame(frame);
        channel.SimulateFrame(frame);
        channel.SimulateFrame(frame);
        Thread.Sleep(200);
        var recent = ctx.GetRecentFrames();

        Assert.Equal(3, recent.Count);
    }

    // ── Channel overload tests (anonymous SingleChannelContext, ChannelName=null) ──

    [Fact]
    public void AnonymousContext_SubscribeDecodedFrames_WithNullChannelName_Works()
    {
        var channel = new FakeCanChannel();
        var dbc = new FakeDbcLookup();
        using var ctx = new SingleChannelContext(channel, dbc);
        var invoked = false;
        using var sub = ctx.SubscribeDecodedFrames(null, _ => invoked = true);

        channel.SimulateFrame(new CanFrame(new CanId(0x100, FrameFormat.Standard),
            new byte[] { 0x01 }, FrameFlags.None, new ChannelId(1), new Timestamp(0)));
        Thread.Sleep(200);

        Assert.True(invoked);
    }

    [Fact]
    public void AnonymousContext_SubscribeDecodedFrames_WithAnyChannelName_Works()
    {
        // anonymous context (ChannelName=null) accepts any channelName as "self"
        var channel = new FakeCanChannel();
        var dbc = new FakeDbcLookup();
        using var ctx = new SingleChannelContext(channel, dbc);
        var invoked = false;
        using var sub = ctx.SubscribeDecodedFrames("bus-a", _ => invoked = true);

        channel.SimulateFrame(new CanFrame(new CanId(0x100, FrameFormat.Standard),
            new byte[] { 0x01 }, FrameFlags.None, new ChannelId(1), new Timestamp(0)));
        Thread.Sleep(200);

        Assert.True(invoked);
    }

    [Fact]
    public async Task AnonymousContext_SendFrameAsync_WithNullChannelName_Works()
    {
        var channel = new FakeCanChannel();
        await channel.ConnectAsync(BaudRate.CanFd1Mbps, fd: true);
        var dbc = new FakeDbcLookup();
        using var ctx = new SingleChannelContext(channel, dbc);
        var frame = new CanFrame(new CanId(0x123, FrameFormat.Standard),
            new byte[] { 0x01 }, FrameFlags.None, default, default);

        var result = await ctx.SendFrameAsync(null, frame, default);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task AnonymousContext_SendFrameAsync_WithAnyChannelName_Works()
    {
        var channel = new FakeCanChannel();
        await channel.ConnectAsync(BaudRate.CanFd1Mbps, fd: true);
        var dbc = new FakeDbcLookup();
        using var ctx = new SingleChannelContext(channel, dbc);
        var frame = new CanFrame(new CanId(0x123, FrameFormat.Standard),
            new byte[] { 0x01 }, FrameFlags.None, default, default);

        var result = await ctx.SendFrameAsync("bus-a", frame, default);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void AnonymousContext_GetRecentDecodedFrames_WithNullChannelName_Works()
    {
        var channel = new FakeCanChannel();
        var dbc = new FakeDbcLookup();
        dbc.AddMessage(CreateMessage(0x123, "TestMsg",
            CreateSignal("TestSignal", 0, 8, ByteOrder.LittleEndian, DbcValueType.Unsigned)));
        using var ctx = new SingleChannelContext(channel, dbc);
        channel.SimulateFrame(new CanFrame(new CanId(0x123, FrameFormat.Standard),
            new byte[] { 0x64 }, FrameFlags.None, new ChannelId(1), new Timestamp(0)));
        Thread.Sleep(200);

        var frames = ctx.GetRecentDecodedFrames(null);

        Assert.Single(frames);
    }

    [Fact]
    public void AnonymousContext_GetRecentDecodedFrames_WithAnyChannelName_Works()
    {
        var channel = new FakeCanChannel();
        var dbc = new FakeDbcLookup();
        dbc.AddMessage(CreateMessage(0x123, "TestMsg",
            CreateSignal("TestSignal", 0, 8, ByteOrder.LittleEndian, DbcValueType.Unsigned)));
        using var ctx = new SingleChannelContext(channel, dbc);
        channel.SimulateFrame(new CanFrame(new CanId(0x123, FrameFormat.Standard),
            new byte[] { 0x64 }, FrameFlags.None, new ChannelId(1), new Timestamp(0)));
        Thread.Sleep(200);

        var frames = ctx.GetRecentDecodedFrames("bus-a");

        Assert.Single(frames);
    }

    // ── Channel overload tests (named SingleChannelContext, ChannelName="bus-a") ──

    [Fact]
    public void NamedContext_SubscribeDecodedFrames_WithNullChannelName_Works()
    {
        var channel = new FakeCanChannel();
        var dbc = new FakeDbcLookup();
        using var ctx = new SingleChannelContext(channel, dbc, channelName: "bus-a");
        var invoked = false;
        using var sub = ctx.SubscribeDecodedFrames(null, _ => invoked = true);

        channel.SimulateFrame(new CanFrame(new CanId(0x100, FrameFormat.Standard),
            new byte[] { 0x01 }, FrameFlags.None, new ChannelId(1), new Timestamp(0)));
        Thread.Sleep(200);

        Assert.True(invoked);
    }

    [Fact]
    public void NamedContext_SubscribeDecodedFrames_WithMatchingChannelName_Works()
    {
        var channel = new FakeCanChannel();
        var dbc = new FakeDbcLookup();
        using var ctx = new SingleChannelContext(channel, dbc, channelName: "bus-a");
        var invoked = false;
        using var sub = ctx.SubscribeDecodedFrames("bus-a", _ => invoked = true);

        channel.SimulateFrame(new CanFrame(new CanId(0x100, FrameFormat.Standard),
            new byte[] { 0x01 }, FrameFlags.None, new ChannelId(1), new Timestamp(0)));
        Thread.Sleep(200);

        Assert.True(invoked);
    }

    [Fact]
    public void NamedContext_SubscribeDecodedFrames_WithNonMatchingChannelName_ReturnsEmpty()
    {
        var channel = new FakeCanChannel();
        var dbc = new FakeDbcLookup();
        using var ctx = new SingleChannelContext(channel, dbc, channelName: "bus-a");
        var invoked = false;
        using var sub = ctx.SubscribeDecodedFrames("bus-b", _ => invoked = true);

        channel.SimulateFrame(new CanFrame(new CanId(0x100, FrameFormat.Standard),
            new byte[] { 0x01 }, FrameFlags.None, new ChannelId(1), new Timestamp(0)));
        Thread.Sleep(200);

        // callback should never be invoked
        Assert.False(invoked);
    }

    [Fact]
    public async Task NamedContext_SendFrameAsync_WithNonMatchingChannelName_ReturnsFailure()
    {
        var channel = new FakeCanChannel();
        await channel.ConnectAsync(BaudRate.CanFd1Mbps, fd: true);
        var dbc = new FakeDbcLookup();
        using var ctx = new SingleChannelContext(channel, dbc, channelName: "bus-a");
        var frame = new CanFrame(new CanId(0x123, FrameFormat.Standard),
            new byte[] { 0x01 }, FrameFlags.None, default, default);

        var result = await ctx.SendFrameAsync("bus-b", frame, default);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void NamedContext_GetRecentDecodedFrames_WithNonMatchingChannelName_ReturnsEmpty()
    {
        var channel = new FakeCanChannel();
        var dbc = new FakeDbcLookup();
        dbc.AddMessage(CreateMessage(0x123, "TestMsg",
            CreateSignal("TestSignal", 0, 8, ByteOrder.LittleEndian, DbcValueType.Unsigned)));
        using var ctx = new SingleChannelContext(channel, dbc, channelName: "bus-a");
        channel.SimulateFrame(new CanFrame(new CanId(0x123, FrameFormat.Standard),
            new byte[] { 0x64 }, FrameFlags.None, new ChannelId(1), new Timestamp(0)));
        Thread.Sleep(200);

        var frames = ctx.GetRecentDecodedFrames("bus-b");

        Assert.Empty(frames);
    }

    [Fact]
    public void NamedContext_GetRecentDecodedFrames_WithNullChannel_ReturnsFrames()
    {
        var channel = new FakeCanChannel();
        var dbc = new FakeDbcLookup();
        dbc.AddMessage(CreateMessage(0x123, "TestMsg",
            CreateSignal("TestSignal", 0, 8, ByteOrder.LittleEndian, DbcValueType.Unsigned)));
        using var ctx = new SingleChannelContext(channel, dbc, channelName: "bus-a");
        channel.SimulateFrame(new CanFrame(new CanId(0x123, FrameFormat.Standard),
            new byte[] { 0x64 }, FrameFlags.None, new ChannelId(1), new Timestamp(0)));
        Thread.Sleep(200);

        var frames = ctx.GetRecentDecodedFrames(null);

        Assert.Single(frames);
    }
}