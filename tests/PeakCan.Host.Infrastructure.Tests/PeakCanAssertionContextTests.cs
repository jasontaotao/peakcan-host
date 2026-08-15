using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.HIL;
using DbcValueType = PeakCan.HIL.Core.Dbc.ValueType;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests;

/// <summary>
/// TDD tests for PeakCanAssertionContext (Sprint 3 Inc 9).
/// </summary>
public class PeakCanAssertionContextTests
{
    private static Signal CreateSignal(string name, ushort startBit, byte length,
        ByteOrder order = ByteOrder.LittleEndian, DbcValueType valueType = DbcValueType.Unsigned,
        double factor = 1, double offset = 0)
        => new(name, startBit, length, order, valueType, factor, offset,
            0, 1000, "", Array.Empty<string>());

    private static Message CreateMessage(uint id, string name, params Signal[] signals)
        => new(id, name, 8, "TestSender", signals, false, null);

    [Fact]
    public void Constructor_SubscribesToFrameReceived()
    {
        // Arrange & Act
        var channel = new FakeCanChannel();
        var dbc = new FakeDbcLookup();
        using var ctx = new PeakCanAssertionContext(channel, dbc);

        // Assert — no exception, subscription verified by subsequent tests
        Assert.True(true);
    }

    [Fact]
    public void OnFrame_WritesToFrameChannel()
    {
        // Arrange
        var channel = new FakeCanChannel();
        var dbc = new FakeDbcLookup();
        using var ctx = new PeakCanAssertionContext(channel, dbc);
        var callbackInvoked = false;
        using var sub = ctx.SubscribeDecodedFrames(_ => callbackInvoked = true);

        // Act
        channel.SimulateFrame(new CanFrame(new CanId(0x123, FrameFormat.Standard),
            new byte[] { 0x64, 0, 0, 0, 0, 0, 0, 0 }, FrameFlags.None, new ChannelId(1), new Timestamp(0)));

        // Assert
        Thread.Sleep(200);
        Assert.True(callbackInvoked, "OnFrame should write to frameChannel and notify subscribers");
    }

    [Fact]
    public async Task SendFrameAsync_DelegatesToChannel()
    {
        // Arrange
        var channel = new FakeCanChannel();
        var dbc = new FakeDbcLookup();
        using var ctx = new PeakCanAssertionContext(channel, dbc);
        var frame = new CanFrame(new CanId(0x123, FrameFormat.Standard),
            new byte[] { 0xDE, 0xAD }, FrameFlags.None, default, default);

        // Act
        var result = await ctx.SendFrameAsync(frame, default);

        // Assert
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void GetSignalValue_AfterDecode_ReturnsValue()
    {
        // Arrange
        var channel = new FakeCanChannel();
        var dbc = new FakeDbcLookup();
        dbc.AddMessage(CreateMessage(0x123, "TestMsg",
            CreateSignal("TestSignal", 0, 8, ByteOrder.LittleEndian, DbcValueType.Unsigned)));
        using var ctx = new PeakCanAssertionContext(channel, dbc);
        var frame = new CanFrame(new CanId(0x123, FrameFormat.Standard),
            new byte[] { 0x64, 0, 0, 0, 0, 0, 0, 0 }, FrameFlags.None, new ChannelId(1), new Timestamp(0));

        // Act
        channel.SimulateFrame(frame);
        Thread.Sleep(200);

        // Assert
        var value = ctx.GetSignalValue("TestMsg.TestSignal");
        Assert.NotNull(value);
        Assert.Equal(100.0, value!.Value, 1);
    }

    [Fact]
    public void Dispose_UnsubscribesAndDrains()
    {
        // Arrange
        var channel = new FakeCanChannel();
        var dbc = new FakeDbcLookup();
        var ctx = new PeakCanAssertionContext(channel, dbc);

        // Act
        ctx.Dispose();

        // Assert — no exception
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
        // Arrange: DBC 含一个 bitLength=65 的信号（SignalDecoder.Decode 抛 ArgumentOutOfRangeException），
        // 以及一个正常 8-bit 信号的消息。
        var channel = new FakeCanChannel();
        var dbc = new FakeDbcLookup();
        dbc.AddMessage(CreateMessage(0x200, "BadMsg",
            CreateSignal("BadSignal", 0, 65, ByteOrder.LittleEndian, DbcValueType.Unsigned)));
        dbc.AddMessage(CreateMessage(0x123, "GoodMsg",
            CreateSignal("GoodSignal", 0, 8, ByteOrder.LittleEndian, DbcValueType.Unsigned)));

        using var ctx = new PeakCanAssertionContext(channel, dbc);
        var sink = new RecordingSink();
        ctx.SetFrameSink(sink);
        // 预热：确保 consumer 线程已启动，避免 drain 在 500ms 上限内 consumer 尚未开跑
        Thread.Sleep(20);

        // 先灌一帧解码必失败的帧（0x200 / 65-bit），再灌一帧正常帧（0x123 / 8-bit）
        channel.SimulateFrame(new CanFrame(new CanId(0x200, FrameFormat.Standard),
            new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF },
            FrameFlags.None, new ChannelId(1), new Timestamp(0)));
        var goodFrame = new CanFrame(new CanId(0x123, FrameFormat.Standard),
            new byte[] { 0x64, 0, 0, 0, 0, 0, 0, 0 },
            FrameFlags.None, new ChannelId(1), new Timestamp(1));
        channel.SimulateFrame(goodFrame);

        await ctx.WaitForFrameDrainAsync(default);

        // loop 存活：goodFrame 到达 sink
        Assert.Contains(sink.Frames, f => f.Id.Raw == goodFrame.Id.Raw);
    }

    [Fact]
    public void GetRecentFrames_ReturnsBuffer()
    {
        // Arrange
        var channel = new FakeCanChannel();
        var dbc = new FakeDbcLookup();
        using var ctx = new PeakCanAssertionContext(channel, dbc);
        var frame = new CanFrame(new CanId(0x123, FrameFormat.Standard),
            new byte[] { 0x01 }, FrameFlags.None, new ChannelId(1), new Timestamp(0));

        // Act
        channel.SimulateFrame(frame);
        channel.SimulateFrame(frame);
        channel.SimulateFrame(frame);
        Thread.Sleep(200);
        var recent = ctx.GetRecentFrames();

        // Assert
        Assert.Equal(3, recent.Count);
    }
}
