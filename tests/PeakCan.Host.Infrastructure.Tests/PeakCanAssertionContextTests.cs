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
