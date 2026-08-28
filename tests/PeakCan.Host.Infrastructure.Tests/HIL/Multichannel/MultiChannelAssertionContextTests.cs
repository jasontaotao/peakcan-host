using System.Collections.Concurrent;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.HIL;
using DbcValueType = PeakCan.HIL.Core.Dbc.ValueType;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Multichannel;

/// <summary>
/// TDD tests for MultiChannelAssertionContext: routing by channelName,
/// ResolveChannelId, sink fan-out, and default-channel fallback.
/// </summary>
public class MultiChannelAssertionContextTests
{
    private static Signal CreateSignal(string name, ushort startBit, byte length,
        ByteOrder order = ByteOrder.LittleEndian, DbcValueType valueType = DbcValueType.Unsigned,
        double factor = 1, double offset = 0)
        => new(name, startBit, length, order, valueType, factor, offset,
            0, 1000, "", Array.Empty<string>());

    private static Message CreateMessage(uint id, string name, params Signal[] signals)
        => new(id, name, 8, "TestSender", signals, false, null);

    private sealed class RecordingSink : IHilFrameSink
    {
        // 多通道扇出时，多个 SingleChannelContext ConsumerLoop 线程并发 Write 同一 sink。
        // List<T>.Add 非线程安全 → 并发丢帧。用 ConcurrentBag 线程安全。
        public ConcurrentBag<CanFrame> Frames { get; } = new();
        public void Write(CanFrame f) => Frames.Add(f);
        public void Dispose() { }
    }

    /// <summary>Build a 2-channel MultiChannelAssertionContext for testing.</summary>
    private static (MultiChannelAssertionContext Ctx, FakeCanChannel ChA, FakeCanChannel ChB) CreateTwoChannelContext()
    {
        var chA = new FakeCanChannel(handle: 0x51);
        var chB = new FakeCanChannel(handle: 0x52);
        var dbcA = new FakeDbcLookup();
        var dbcB = new FakeDbcLookup();
        var ctxA = new SingleChannelContext(chA, dbcA, channelName: "bus-a");
        var ctxB = new SingleChannelContext(chB, dbcB, channelName: "bus-b");
        var multi = new MultiChannelAssertionContext(
            new Dictionary<string, SingleChannelContext>
            {
                ["bus-a"] = ctxA,
                ["bus-b"] = ctxB,
            },
            defaultChannelName: "bus-a");
        return (multi, chA, chB);
    }

    [Fact]
    public void Constructor_WithEmptyChannels_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new MultiChannelAssertionContext(new Dictionary<string, SingleChannelContext>()));
    }

    [Fact]
    public void Constructor_WithDefaultChannelNameNotFound_Throws()
    {
        var ctx = new SingleChannelContext(new FakeCanChannel(), new FakeDbcLookup(), channelName: "bus-a");
        Assert.Throws<ArgumentException>(() =>
            new MultiChannelAssertionContext(
                new Dictionary<string, SingleChannelContext> { ["bus-a"] = ctx },
                defaultChannelName: "bus-b"));
    }

    [Fact]
    public void ResolveChannelId_WithNull_ReturnsDefaultChannelId()
    {
        var (multi, chA, _) = CreateTwoChannelContext();
        var id = multi.ResolveChannelId(null);
        Assert.Equal(chA.Id, id);
    }

    [Fact]
    public void ResolveChannelId_WithMatchingName_ReturnsCorrectChannelId()
    {
        var (multi, _, chB) = CreateTwoChannelContext();
        var id = multi.ResolveChannelId("bus-b");
        Assert.Equal(chB.Id, id);
    }

    [Fact]
    public void ResolveChannelId_WithUnknownName_ReturnsNone()
    {
        var (multi, _, _) = CreateTwoChannelContext();
        var id = multi.ResolveChannelId("unknown-bus");
        Assert.Equal(ChannelId.None, id);
    }

    [Fact]
    public async Task SendFrameAsync_NullChannelName_RoutesToDefault()
    {
        var (multi, chA, chB) = CreateTwoChannelContext();
        await chA.ConnectAsync(BaudRate.CanFd1Mbps, fd: true);
        await chB.ConnectAsync(BaudRate.CanFd1Mbps, fd: true);
        var frame = new CanFrame(new CanId(0x100, FrameFormat.Standard),
            new byte[] { 0x01 }, FrameFlags.None, default, default);

        var result = await multi.SendFrameAsync(null, frame, default);

        Assert.True(result.IsSuccess);
        // Default = bus-a, so chA should have received it
        Assert.Contains(chA.WrittenFrames, f => f.Id.Raw == 0x100);
        // chB should NOT have received it
        Assert.DoesNotContain(chB.WrittenFrames, f => f.Id.Raw == 0x100);
    }

    [Fact]
    public async Task SendFrameAsync_SpecificChannel_RoutesCorrectly()
    {
        var (multi, chA, chB) = CreateTwoChannelContext();
        await chA.ConnectAsync(BaudRate.CanFd1Mbps, fd: true);
        await chB.ConnectAsync(BaudRate.CanFd1Mbps, fd: true);
        var frameA = new CanFrame(new CanId(0x100, FrameFormat.Standard),
            new byte[] { 0x01 }, FrameFlags.None, default, default);
        var frameB = new CanFrame(new CanId(0x200, FrameFormat.Standard),
            new byte[] { 0x02 }, FrameFlags.None, default, default);

        await multi.SendFrameAsync("bus-a", frameA, default);
        await multi.SendFrameAsync("bus-b", frameB, default);

        Assert.Contains(chA.WrittenFrames, f => f.Id.Raw == 0x100);
        Assert.Contains(chB.WrittenFrames, f => f.Id.Raw == 0x200);
        // No cross-contamination
        Assert.DoesNotContain(chA.WrittenFrames, f => f.Id.Raw == 0x200);
        Assert.DoesNotContain(chB.WrittenFrames, f => f.Id.Raw == 0x100);
    }

    [Fact]
    public async Task SendFrameAsync_UnknownChannel_ReturnsFailure()
    {
        var (multi, _, _) = CreateTwoChannelContext();
        var frame = new CanFrame(new CanId(0x100, FrameFormat.Standard),
            new byte[] { 0x01 }, FrameFlags.None, default, default);

        var result = await multi.SendFrameAsync("unknown", frame, default);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void SubscribeDecodedFrames_SpecificChannel_ReceivesOnlyFromThatChannel()
    {
        var chA = new FakeCanChannel(handle: 0x51);
        var chB = new FakeCanChannel(handle: 0x52);
        var dbcA = new FakeDbcLookup();
        var dbcB = new FakeDbcLookup();

        // Build a version where bus-a has DBC 0x100 and bus-b has DBC 0x200
        // We need separate contexts with different DBCs
        var ctxA = new SingleChannelContext(chA, MakeDbc(0x100, "MsgA"), channelName: "bus-a");
        var ctxB = new SingleChannelContext(chB, MakeDbc(0x200, "MsgB"), channelName: "bus-b");
        var multi = new MultiChannelAssertionContext(
            new Dictionary<string, SingleChannelContext>
            {
                ["bus-a"] = ctxA,
                ["bus-b"] = ctxB,
            },
            defaultChannelName: "bus-a");

        var framesA = new List<DecodedFrame>();
        var framesB = new List<DecodedFrame>();
        using var subA = multi.SubscribeDecodedFrames("bus-a", f => framesA.Add(f));
        using var subB = multi.SubscribeDecodedFrames("bus-b", f => framesB.Add(f));

        // Send a frame on bus-a (0x100)
        chA.SimulateFrame(new CanFrame(new CanId(0x100, FrameFormat.Standard),
            new byte[] { 0x64 }, FrameFlags.None, new ChannelId(0x51), new Timestamp(0)));
        // Send a frame on bus-b (0x200)
        chB.SimulateFrame(new CanFrame(new CanId(0x200, FrameFormat.Standard),
            new byte[] { 0x64 }, FrameFlags.None, new ChannelId(0x52), new Timestamp(1)));

        Thread.Sleep(300);

        // bus-a subscriber should have received bus-a's frame
        Assert.Single(framesA);
        Assert.Equal(0x100u, framesA[0].Frame.Id.Raw);
        // bus-b subscriber should have received bus-b's frame
        Assert.Single(framesB);
        Assert.Equal(0x200u, framesB[0].Frame.Id.Raw);
    }

    [Fact]
    public void GetRecentDecodedFrames_SpecificChannel_ReturnsOnlyThatChannelsFrames()
    {
        // Create contexts with DBCs so frames get decoded
        var chA = new FakeCanChannel(handle: 0x51);
        var chB = new FakeCanChannel(handle: 0x52);
        var ctxA = new SingleChannelContext(chA, MakeDbc(0x100, "MsgA"), channelName: "bus-a");
        var ctxB = new SingleChannelContext(chB, MakeDbc(0x200, "MsgB"), channelName: "bus-b");
        var multi = new MultiChannelAssertionContext(
            new Dictionary<string, SingleChannelContext>
            {
                ["bus-a"] = ctxA,
                ["bus-b"] = ctxB,
            },
            defaultChannelName: "bus-a");

        // Send frames on both channels
        chA.SimulateFrame(new CanFrame(new CanId(0x100, FrameFormat.Standard),
            new byte[] { 0x64 }, FrameFlags.None, new ChannelId(0x51), new Timestamp(0)));
        chB.SimulateFrame(new CanFrame(new CanId(0x200, FrameFormat.Standard),
            new byte[] { 0x64 }, FrameFlags.None, new ChannelId(0x52), new Timestamp(1)));
        Thread.Sleep(300);

        var framesA = multi.GetRecentDecodedFrames("bus-a");
        var framesB = multi.GetRecentDecodedFrames("bus-b");

        Assert.Single(framesA);
        Assert.Equal(0x100u, framesA[0].Frame.Id.Raw);

        Assert.Single(framesB);
        Assert.Equal(0x200u, framesB[0].Frame.Id.Raw);
    }

    [Fact]
    public void SubscribeDecodedFrames_NullChannelName_RoutesToDefault()
    {
        var (multi, chA, chB) = CreateTwoChannelContext();
        var invoked = false;
        using var sub = multi.SubscribeDecodedFrames(null, _ => invoked = true);

        chA.SimulateFrame(new CanFrame(new CanId(0x100, FrameFormat.Standard),
            new byte[] { 0x01 }, FrameFlags.None, new ChannelId(0x51), new Timestamp(0)));
        Thread.Sleep(200);

        Assert.True(invoked, "null channelName should route to default channel (bus-a)");
    }

    [Fact]
    public void GetRecentDecodedFrames_NullChannelName_ReturnsDefaultChannelFrames()
    {
        var (multi, _, _) = CreateTwoChannelContext();
        var frames = multi.GetRecentDecodedFrames(null);
        Assert.NotNull(frames);
    }

    // ── Sink fan-out ──

    [Fact]
    public async Task SetFrameSink_NullChannelName_FansOutToAllChannels()
    {
        var (multi, chA, chB) = CreateTwoChannelContext();
        var sink = new RecordingSink();
        // 预热：确保 consumer 线程已启动
        await Task.Delay(20);

        multi.SetFrameSink(null, sink);

        // Send frames on both channels — they should all reach the same sink
        chA.SimulateFrame(new CanFrame(new CanId(0x100, FrameFormat.Standard),
            new byte[] { 0x01 }, FrameFlags.None, new ChannelId(0x51), new Timestamp(0)));
        chB.SimulateFrame(new CanFrame(new CanId(0x200, FrameFormat.Standard),
            new byte[] { 0x02 }, FrameFlags.None, new ChannelId(0x52), new Timestamp(1)));

        // Use WaitForFrameDrainAsync to wait for consumer to drain
        await multi.WaitForFrameDrainAsync(default);

        // Both frames should be in the sink
        Assert.Contains(sink.Frames, f => f.Id.Raw == 0x100);
        Assert.Contains(sink.Frames, f => f.Id.Raw == 0x200);
    }

    [Fact]
    public async Task SetFrameSink_SpecificChannel_MountsOnlyOnThatChannel()
    {
        var (multi, chA, chB) = CreateTwoChannelContext();
        var sink = new RecordingSink();
        await Task.Delay(20);
        multi.SetFrameSink("bus-a", sink);

        // Send frame on bus-a (should reach sink)
        chA.SimulateFrame(new CanFrame(new CanId(0x100, FrameFormat.Standard),
            new byte[] { 0x01 }, FrameFlags.None, new ChannelId(0x51), new Timestamp(0)));
        // Send frame on bus-b (should NOT reach sink)
        chB.SimulateFrame(new CanFrame(new CanId(0x200, FrameFormat.Standard),
            new byte[] { 0x02 }, FrameFlags.None, new ChannelId(0x52), new Timestamp(1)));

        await multi.WaitForFrameDrainAsync(default);

        Assert.Contains(sink.Frames, f => f.Id.Raw == 0x100);
        Assert.DoesNotContain(sink.Frames, f => f.Id.Raw == 0x200);
    }

    // ── GetSignalValue channel routing (G1) ──

    private static (MultiChannelAssertionContext Multi, FakeCanChannel ChA, FakeCanChannel ChB) CreateTwoChannelContextWithDbc()
    {
        // 同名信号 "Msg.Sig" 两通道不同值：bus-a=100 (0x64)、bus-b=200 (0xC8)
        var chA = new FakeCanChannel(handle: 0x51);
        var chB = new FakeCanChannel(handle: 0x52);
        var ctxA = new SingleChannelContext(chA, MakeDbc(0x100, "Msg"), channelName: "bus-a");
        var ctxB = new SingleChannelContext(chB, MakeDbc(0x200, "Msg"), channelName: "bus-b");
        var multi = new MultiChannelAssertionContext(
            new Dictionary<string, SingleChannelContext> { ["bus-a"] = ctxA, ["bus-b"] = ctxB },
            defaultChannelName: "bus-a");
        return (multi, chA, chB);
    }

    [Fact]
    public async Task GetSignalValue_SpecificChannel_ReturnsThatChannelsValue()
    {
        var (multi, chA, chB) = CreateTwoChannelContextWithDbc();
        using var _ = multi;

        chA.SimulateFrame(new CanFrame(new CanId(0x100, FrameFormat.Standard),
            new byte[] { 0x64 }, FrameFlags.None, new ChannelId(0x51), new Timestamp(0)));
        chB.SimulateFrame(new CanFrame(new CanId(0x200, FrameFormat.Standard),
            new byte[] { 0xC8 }, FrameFlags.None, new ChannelId(0x52), new Timestamp(1)));
        await multi.WaitForFrameDrainAsync(default);

        // DIM 成员须经接口引用调用（concrete 类型看不到 DIM 默认）；executor 均持 IAssertionContext
        IAssertionContext iface = multi;
        Assert.Equal(100.0, iface.GetSignalValue("bus-a", "Msg.Sig"));
        Assert.Equal(200.0, iface.GetSignalValue("bus-b", "Msg.Sig"));
    }

    [Fact]
    public async Task GetSignalValue_NullChannelName_RoutesToDefault()
    {
        var (multi, chA, _) = CreateTwoChannelContextWithDbc();
        using var _ = multi;

        chA.SimulateFrame(new CanFrame(new CanId(0x100, FrameFormat.Standard),
            new byte[] { 0x64 }, FrameFlags.None, new ChannelId(0x51), new Timestamp(0)));
        await multi.WaitForFrameDrainAsync(default);

        // null → 默认通道（bus-a）
        IAssertionContext iface = multi;
        Assert.Equal(100.0, iface.GetSignalValue(null, "Msg.Sig"));
    }

    [Fact]
    public void GetSignalValue_UnknownChannel_ThrowsKeyNotFoundException()
    {
        var (multi, _, _) = CreateTwoChannelContextWithDbc();
        using var _ = multi;
        IAssertionContext iface = multi;
        Assert.Throws<KeyNotFoundException>(() => iface.GetSignalValue("unknown-bus", "Msg.Sig"));
    }

    // ── Disconnect ──

    [Fact]
    public async Task DisconnectAllAsync_DisconnectsAllChannels()
    {
        // Bug-1：多通道 run 结束时所有通道的 PeakCanChannel 必须 DisconnectAsync，
        // 否则非首通道 PCAN handle 泄漏 + 读循环空转。
        var (multi, chA, chB) = CreateTwoChannelContext();
        await chA.ConnectAsync(BaudRate.CanFd1Mbps, fd: true);
        await chB.ConnectAsync(BaudRate.CanFd1Mbps, fd: true);
        Assert.True(chA.IsConnected, "chA should be connected");
        Assert.True(chB.IsConnected, "chB should be connected");

        await multi.DisconnectAllAsync(default);

        Assert.False(chA.IsConnected, "chA should be disconnected");
        Assert.False(chB.IsConnected, "chB should be disconnected");
    }

    // ── Helpers ──

    private static FakeDbcLookup MakeDbc(uint id, string name)
    {
        var dbc = new FakeDbcLookup();
        dbc.AddMessage(new Message(id, name, 8, "Test", new[]
        {
            new Signal("Sig", 0, 8, ByteOrder.LittleEndian, DbcValueType.Unsigned,
                1, 0, 0, 1000, "", Array.Empty<string>())
        }, false, null));
        return dbc;
    }
}