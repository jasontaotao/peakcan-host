using Microsoft.Extensions.DependencyInjection;
using PeakCan.Host.Core;
using PeakCan.Host.Core.HIL;
using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Core.HIL.Serialization;
using PeakCan.Host.Infrastructure.Cli;
using PeakCan.Host.Infrastructure.HIL;
using System.Text.Json;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests.HIL;

public class EcuE2EDiagnosticTest
{
    [Fact]
    public async Task E2E_VirtualEcu_via_HeadlessHostBuilder()
    {
        // 1. Create minimal DBC
        var dbcPath = Path.Combine(Path.GetTempPath(), $"e2e_{Guid.NewGuid():N}.dbc");
        File.WriteAllText(dbcPath, """
            VERSION "1.0";
            NS_ :
            BS_:
            BU_: ECU
            BO_ 256 TestMsg: 8 ECU
             SG_ TestSignal : 0|8@1+ (1,0) [0|255] "V"  ECU
            """);

        // 2. Create ECU script JSON (HIL perspective: requestId=0x7E0, responseId=0x7E8)
        var ecuPath = Path.Combine(Path.GetTempPath(), $"e2e_{Guid.NewGuid():N}.json");
        File.WriteAllText(ecuPath, """
            {
              "name": "TestEcu",
              "canIds": { "requestId": "0x7E0", "responseId": "0x7E8" },
              "rules": [
                { "serviceId": "0x3E", "subFunction": 0, "responseData": [126] }
              ]
            }
            """);

        // 3. Create test suite JSON (sendFrame + waitForFrame)
        var suitePath = Path.Combine(Path.GetTempPath(), $"e2e_{Guid.NewGuid():N}.json");
        File.WriteAllText(suitePath, """
            {
              "name": "EcuE2ESuite",
              "cases": [
                {
                  "id": "case_1",
                  "name": "SendAndReceive",
                  "steps": [
                    {
                      "parameters": {
                        "$kind": "sendFrame",
                        "id": { "raw": 2016, "format": "Standard", "type": "Data" },
                        "data": [2, 62, 0],
                        "fd": false
                      }
                    },
                    {
                      "parameters": {
                        "$kind": "expectFrame",
                        "id": { "raw": 2024, "format": "Standard", "type": "Data" },
                        "timeoutMs": 3000
                      }
                    }
                  ]
                }
              ],
              "globalCaseFixtureKeys": [],
              "suiteFixtureKeys": [],
              "config": { "failurePolicy": "ContinueAll", "continueAfterSetupFailure": true },
              "timeoutMs": 0
            }
            """);

        try
        {
            // 4. Build via HeadlessHostBuilder
            var cli = new CliArgs(dbcPath, suitePath, EcuScriptPath: ecuPath);
            using var host = HeadlessHostBuilder.Build(cli);

            var engine = host.Services.GetRequiredService<TestSuiteEngine>();
            var channel = host.Services.GetRequiredService<ICanChannel>();
            var ctx = host.Services.GetRequiredService<IAssertionContext>();

            // 5. Connect and run
            await channel.ConnectAsync(BaudRate.Can500kbps, false);

            // Diagnostic: manually send a frame and check if VirtualEcu responds
            bool ecuSawFrame = false;
            channel.FrameReceived += f =>
            {
                if (f.Id.Raw == 0x7E8) ecuSawFrame = true;
            };

            var requestFrame = new CanFrame(
                new CanId(0x7E0, FrameFormat.Standard),
                new ReadOnlyMemory<byte>(new byte[] { 0x02, 0x3E, 0x00 }),
                FrameFlags.None, ChannelId.None, new Timestamp(0));
            await channel.WriteAsync(requestFrame);
            await Task.Delay(500); // Wait for ECU response

            Assert.True(ecuSawFrame, "VirtualEcu should have sent a response frame on 0x7E8");

            // 6. Execute test suite
            var suiteJson = await File.ReadAllTextAsync(suitePath);
            var suite = JsonSerializer.Deserialize<TestSuite>(suiteJson, HILJsonOptions.Default)!;
            var result = await engine.ExecuteAsync(suite, ctx, new TestSuiteConfig(), null, default);

            Assert.True(result.AllPassed, $"Expected all passed, got: {result.PassedCases}/{result.TotalCases} passed");

            await channel.DisconnectAsync();
        }
        finally
        {
            File.Delete(dbcPath);
            File.Delete(ecuPath);
            File.Delete(suitePath);
        }
    }
}
