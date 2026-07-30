using PeakCan.Host.Core;
using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.HIL;

namespace PeakCan.Host.Infrastructure.Tests.HIL;

public class HeadlessHostBuilderStatefulTests
{
    private static string WriteTempScript(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"hil_stateful_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public async Task HeadlessHostBuilder_EcuMode_Stateful_EndToEnd()
    {
        // Arrange: write a stateful ECU script to temp file
        var scriptJson = """
        {
            "name": "BMS_Secure",
            "canIds": { "requestId": "0x7E0", "responseId": "0x7E8" },
            "states": [
                {
                    "name": "default",
                    "transitions": [
                        { "serviceId": "0x27", "subFunction": 1, "response": { "$type": "static", "data": [103, 1, 17, 34, 51, 68] }, "toState": "seedSent" },
                        { "serviceId": "0x27", "subFunction": 2, "response": { "$type": "static", "data": [127, 39, 35] } }
                    ]
                },
                {
                    "name": "seedSent",
                    "transitions": [
                        { "serviceId": "0x27", "subFunction": 2, "response": { "$type": "static", "data": [103, 2] }, "toState": "unlocked" }
                    ]
                }
            ]
        }
        """;
        var scriptPath = WriteTempScript(scriptJson);
        try
        {
            // Load script and create ECU directly (simulating what HeadlessHostBuilder does)
            var script = EcuScriptLoader.Load(scriptPath);
            var channel = new CanChannels.VirtualChannel();
            var ecu = new StatefulVirtualEcu(channel, script.CanIds, script.StateMachine);

            var tcs = new TaskCompletionSource<CanFrame>();
            channel.FrameReceived += f =>
            {
                if (f.Id.Raw == 0x7E8) tcs.TrySetResult(f);
            };

            await channel.ConnectAsync(BaudRate.Can500kbps, false);

            // Send 0x27 subFunc 1 (request seed) to 0x7E0
            var requestFrame = new CanFrame(
                new CanId(0x7E0, FrameFormat.Standard),
                new ReadOnlyMemory<byte>(new byte[] { 0x02, 0x27, 0x01 }),
                FrameFlags.None, ChannelId.None, new Timestamp(0));

            await channel.WriteAsync(requestFrame);

            var response = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0x7E8u, response.Id.Raw);
            Assert.Equal("seedSent", ecu.CurrentState);

            await channel.DisposeAsync();
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }
}
