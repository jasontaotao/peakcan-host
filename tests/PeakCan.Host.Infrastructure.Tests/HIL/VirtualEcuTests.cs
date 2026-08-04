using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.Uds.IsoTp;
using PeakCan.Host.Infrastructure.CanChannels;
using PeakCan.Host.Infrastructure.HIL;

namespace PeakCan.Host.Infrastructure.Tests.HIL;

public class VirtualEcuTests
{
    private static CanIdConfig CreateEcuCanIds() => new()
    {
        RequestId = 0x7E8,  // ECU sends responses on 0x7E8
        ResponseId = 0x7E0  // ECU receives requests on 0x7E0
    };

    private static VirtualEcu CreateEcu(params UdsResponseRule[] rules)
    {
        var channel = new VirtualChannel();
        return new VirtualEcu(channel, CreateEcuCanIds(), rules);
    }

    [Fact]
    public async Task Responds_to_single_frame_UDS_request()
    {
        var channel = new VirtualChannel();
        var rule = new UdsResponseRule { ServiceId = 0x3E, SubFunction = 0x00, ResponseData = new byte[] { 0x7E } };
        var ecu = new VirtualEcu(channel, CreateEcuCanIds(), new[] { rule });

        var tcs = new TaskCompletionSource<CanFrame>();
        channel.FrameReceived += f =>
        {
            if (f.Id.Raw == 0x7E8) tcs.TrySetResult(f);
        };

        await channel.ConnectAsync(BaudRate.Can500kbps, false);

        // Send UDS TesterPresent request (SID=0x3E, subFunc=0x00) to 0x7E0
        var requestFrame = new CanFrame(
            new CanId(0x7E0, FrameFormat.Standard),
            new ReadOnlyMemory<byte>(new byte[] { 0x02, 0x3E, 0x00 }), // len=2, SID=0x3E, subFunc=0x00
            FrameFlags.None, ChannelId.None, new Timestamp(0));

        await channel.WriteAsync(requestFrame);

        var response = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0x7E8u, response.Id.Raw);
        Assert.Contains((byte)0x7E, response.Data.ToArray()); // positive response SID

        await channel.DisposeAsync();
    }

    [Fact]
    public async Task Returns_NRC_0x11_when_no_rule_matches()
    {
        var channel = new VirtualChannel();
        var rule = new UdsResponseRule { ServiceId = 0x3E, ResponseData = new byte[] { 0x7E } };
        var ecu = new VirtualEcu(channel, CreateEcuCanIds(), new[] { rule });

        var tcs = new TaskCompletionSource<CanFrame>();
        channel.FrameReceived += f =>
        {
            if (f.Id.Raw == 0x7E8) tcs.TrySetResult(f);
        };

        await channel.ConnectAsync(BaudRate.Can500kbps, false);

        // Send SID=0x10 (no matching rule)
        var requestFrame = new CanFrame(
            new CanId(0x7E0, FrameFormat.Standard),
            new ReadOnlyMemory<byte>(new byte[] { 0x02, 0x10, 0x00 }),
            FrameFlags.None, ChannelId.None, new Timestamp(0));

        await channel.WriteAsync(requestFrame);

        var response = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0x7E8u, response.Id.Raw);
        var data = response.Data.ToArray();
        // ISO-TP single frame: byte[0] = PCI (0x03 = length 3), bytes[1..3] = UDS payload
        Assert.Equal(0x03, data[0]); // ISO-TP PCI: 3 bytes follow
        Assert.Equal(0x7F, data[1]); // NegativeResponse SID
        Assert.Equal(0x10, data[2]); // original SID
        Assert.Equal(0x11, data[3]); // NRC serviceNotSupported

        await channel.DisposeAsync();
    }

    [Fact]
    public async Task ResponseDelayMs_delays_response()
    {
        var channel = new VirtualChannel();
        var rule = new UdsResponseRule
        {
            ServiceId = 0x3E,
            SubFunction = 0x00,
            ResponseData = new byte[] { 0x7E },
            ResponseDelayMs = 100
        };
        var ecu = new VirtualEcu(channel, CreateEcuCanIds(), new[] { rule });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tcs = new TaskCompletionSource<CanFrame>();
        channel.FrameReceived += f =>
        {
            if (f.Id.Raw == 0x7E8) tcs.TrySetResult(f);
        };

        await channel.ConnectAsync(BaudRate.Can500kbps, false);

        var requestFrame = new CanFrame(
            new CanId(0x7E0, FrameFormat.Standard),
            new ReadOnlyMemory<byte>(new byte[] { 0x02, 0x3E, 0x00 }),
            FrameFlags.None, ChannelId.None, new Timestamp(0));

        await channel.WriteAsync(requestFrame);
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds >= 90, $"Expected >= 100ms delay, got {sw.ElapsedMilliseconds}ms");

        await channel.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_unsubscribes_FrameReceived()
    {
        var channel = new VirtualChannel();
        var rule = new UdsResponseRule { ServiceId = 0x3E, SubFunction = 0x00, ResponseData = new byte[] { 0x7E } };
        var ecu = new VirtualEcu(channel, CreateEcuCanIds(), new[] { rule });

        await channel.ConnectAsync(BaudRate.Can500kbps, false);
        ecu.Dispose();

        // After dispose, sending a request should NOT produce a response
        var responseReceived = false;
        channel.FrameReceived += f =>
        {
            if (f.Id.Raw == 0x7E8) responseReceived = true;
        };

        var requestFrame = new CanFrame(
            new CanId(0x7E0, FrameFormat.Standard),
            new ReadOnlyMemory<byte>(new byte[] { 0x02, 0x3E, 0x00 }),
            FrameFlags.None, ChannelId.None, new Timestamp(0));

        await channel.WriteAsync(requestFrame);
        await Task.Delay(200);

        Assert.False(responseReceived);

        await channel.DisposeAsync();
    }

    [Fact]
    public async Task First_matching_rule_wins()
    {
        var channel = new VirtualChannel();
        var rule1 = new UdsResponseRule { ServiceId = 0x3E, SubFunction = 0x00, ResponseData = new byte[] { 0x7E } };
        var rule2 = new UdsResponseRule { ServiceId = 0x3E, SubFunction = 0x00, ResponseData = new byte[] { 0xFF } };
        var ecu = new VirtualEcu(channel, CreateEcuCanIds(), new[] { rule1, rule2 });

        var tcs = new TaskCompletionSource<CanFrame>();
        channel.FrameReceived += f =>
        {
            if (f.Id.Raw == 0x7E8) tcs.TrySetResult(f);
        };

        await channel.ConnectAsync(BaudRate.Can500kbps, false);

        var requestFrame = new CanFrame(
            new CanId(0x7E0, FrameFormat.Standard),
            new ReadOnlyMemory<byte>(new byte[] { 0x02, 0x3E, 0x00 }),
            FrameFlags.None, ChannelId.None, new Timestamp(0));

        await channel.WriteAsync(requestFrame);

        var response = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Contains((byte)0x7E, response.Data.ToArray()); // rule1's response, not rule2's 0xFF

        await channel.DisposeAsync();
    }
}
