using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.Uds.IsoTp;
using PeakCan.Host.Infrastructure.CanChannels;
using PeakCan.Host.Infrastructure.HIL;

namespace PeakCan.Host.Infrastructure.Tests.HIL;

/// <summary>
/// M1 fix: End-to-end integration test for built-in generators through the
/// full EcuScriptLoader → StatefulVirtualEcu pipeline.
/// </summary>
public class BuiltInGeneratorIntegrationTests
{
    private static string StatefulScriptJson => """
    {
        "name": "SecureECU",
        "canIds": { "requestId": "0x7E0", "responseId": "0x7E8" },
        "states": [
            {
                "name": "default",
                "transitions": [
                    { "serviceId": "0x10", "subFunction": 2, "response": { "$type": "static", "data": [80, 2] }, "toState": "locked" }
                ]
            },
            {
                "name": "locked",
                "transitions": [
                    { "serviceId": "0x27", "subFunction": 1, "response": { "$type": "dynamic", "generatorName": "SecurityAccessSeed" }, "toState": "seedSent" },
                    { "serviceId": "0x27", "subFunction": 2, "response": { "$type": "static", "data": [127, 39, 35] }, "comment": "NRC 0x22" }
                ]
            },
            {
                "name": "seedSent",
                "transitions": [
                    { "serviceId": "0x27", "subFunction": 1, "response": { "$type": "dynamic", "generatorName": "SecurityAccessSeed" }, "toState": "seedSent" },
                    { "serviceId": "0x27", "subFunction": 2, "response": { "$type": "dynamic", "generatorName": "SecurityAccessVerifyKey" }, "toState": "unlocked" }
                ]
            },
            {
                "name": "unlocked",
                "transitions": [
                    { "serviceId": "0x2E", "response": { "$type": "static", "data": [110] }, "toState": "unlocked" }
                ]
            }
        ]
    }
    """;

    /// <summary>Extract UDS payload from ISO-TP single frame. Byte[0] is PCI (length), bytes[1..] are UDS payload.</summary>
    private static byte[] ExtractUdsPayload(CanFrame frame)
    {
        var data = frame.Data.ToArray();
        var length = data[0] & 0x0F; // ISO-TP SF PCI: lower nibble = payload length
        return data[1..(1 + length)];
    }

    private static async Task<CanFrame> SendAndReceive(VirtualChannel channel, byte[] udsPayload)
    {
        var tcs = new TaskCompletionSource<CanFrame>();
        channel.FrameReceived += f =>
        {
            if (f.Id.Raw == 0x7E8) tcs.TrySetResult(f);
        };

        // Build ISO-TP single frame: PCI byte + UDS payload
        var frameData = new byte[1 + udsPayload.Length];
        frameData[0] = (byte)(udsPayload.Length & 0x0F);
        udsPayload.CopyTo(frameData, 1);

        var requestFrame = new CanFrame(
            new CanId(0x7E0, FrameFormat.Standard),
            new ReadOnlyMemory<byte>(frameData),
            FrameFlags.None, ChannelId.None, new Timestamp(0));

        await channel.WriteAsync(requestFrame);
        return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task BuiltInGenerators_SecurityAccessSeed_FullPipeline()
    {
        // Arrange
        var script = EcuScriptLoader.Parse(StatefulScriptJson);
        var channel = new VirtualChannel();
        var ecu = new StatefulVirtualEcu(channel, script.CanIds, script.StateMachine);

        await channel.ConnectAsync(BaudRate.Can500kbps, false);

        // Step 0: Move from default to locked
        await SendAndReceive(channel, new byte[] { 0x10, 0x02 });
        Assert.Equal("locked", ecu.CurrentState);

        // Step 1: Request seed (dynamic generator)
        var seedResp = await SendAndReceive(channel, new byte[] { 0x27, 0x01 });
        var payload = ExtractUdsPayload(seedResp);

        // Verify: state transitioned to seedSent
        Assert.Equal("seedSent", ecu.CurrentState);

        // Verify: response contains seed (positive response [0x67, 0x01, seed[0..3]])
        Assert.True(payload.Length >= 3, $"Expected at least 3 bytes, got {payload.Length}");
        Assert.Equal(0x67, payload[0]); // positive response SID
        Assert.Equal(0x01, payload[1]); // subFunc

        await channel.DisposeAsync();
    }

    [Fact]
    public async Task BuiltInGenerators_SecurityAccessVerifyKey_WrongKey_ReturnsNrc35()
    {
        // Arrange
        var script = EcuScriptLoader.Parse(StatefulScriptJson);
        var channel = new VirtualChannel();
        var ecu = new StatefulVirtualEcu(channel, script.CanIds, script.StateMachine);

        await channel.ConnectAsync(BaudRate.Can500kbps, false);

        // Step 0: Move to locked → seedSent
        await SendAndReceive(channel, new byte[] { 0x10, 0x02 });
        await SendAndReceive(channel, new byte[] { 0x27, 0x01 });
        Assert.Equal("seedSent", ecu.CurrentState);

        // Step 1: Send wrong key
        var response = await SendAndReceive(channel, new byte[] { 0x27, 0x02, 0xFF, 0xFF, 0xFF, 0xFF });
        var payload = ExtractUdsPayload(response);

        // Verify: NRC 0x35 (invalidKey)
        // Note: EcuStateMachine always transitions to toState after matching a transition,
        // regardless of whether the response is positive or negative. This is a known design
        // limitation — wrong-key handling requires conditional transitions (future enhancement).
        Assert.Equal(new byte[] { 0x7F, 0x27, 0x35 }, payload);

        await channel.DisposeAsync();
    }

    [Fact]
    public async Task BuiltInGenerators_ClearDtc_ReturnsPositiveResponse()
    {
        // Arrange: script with ClearDtc generator
        var json = """
        {
            "name": "DtcECU",
            "canIds": { "requestId": "0x7E0", "responseId": "0x7E8" },
            "states": [
                {
                    "name": "default",
                    "transitions": [
                        { "serviceId": "0x14", "response": { "$type": "dynamic", "generatorName": "ClearDtc" } }
                    ]
                }
            ]
        }
        """;
        var script = EcuScriptLoader.Parse(json);
        var channel = new VirtualChannel();
        var ecu = new StatefulVirtualEcu(channel, script.CanIds, script.StateMachine);

        await channel.ConnectAsync(BaudRate.Can500kbps, false);

        // Act: Send ClearDtc request (SID 0x14, groupOfDtc = 0xFF 0xFF 0xFF)
        var response = await SendAndReceive(channel, new byte[] { 0x14, 0xFF, 0xFF, 0xFF });
        var payload = ExtractUdsPayload(response);

        // Assert: positive response [0x54]
        Assert.Equal(new byte[] { 0x54 }, payload);

        await channel.DisposeAsync();
    }

    [Fact]
    public async Task BuiltInGenerators_SecurityAccessSeed_ConsistentAcrossCalls()
    {
        // Arrange
        var script = EcuScriptLoader.Parse(StatefulScriptJson);
        var channel = new VirtualChannel();
        var ecu = new StatefulVirtualEcu(channel, script.CanIds, script.StateMachine);

        await channel.ConnectAsync(BaudRate.Can500kbps, false);

        // Move to locked
        await SendAndReceive(channel, new byte[] { 0x10, 0x02 });

        // Request seed twice — should return same seed (cached in context)
        var resp1 = await SendAndReceive(channel, new byte[] { 0x27, 0x01 });
        var payload1 = ExtractUdsPayload(resp1);

        var resp2 = await SendAndReceive(channel, new byte[] { 0x27, 0x01 });
        var payload2 = ExtractUdsPayload(resp2);

        Assert.Equal(payload1, payload2);

        await channel.DisposeAsync();
    }
}
