using Microsoft.Extensions.DependencyInjection;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Serialization;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.Host.Core;
using PeakCan.Host.Core.Uds;
using PeakCan.Host.Infrastructure.Cli;
using PeakCan.Host.Infrastructure.HIL;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests.HIL;

/// <summary>
/// M3.4（spec 2026-09-07 Phase 3）：--key-algorithm builtin 选择入口 +
/// PlaceholderKeyAlgorithm 默认行为回归。
/// tester 模式 host（UdsClient 挂内置 XOR-0xAA）↔ 手挂 StatefulVirtualEcu
/// （默认 SecurityAccessServer 亦为 XOR-0xAA）→ 全握手解锁。
/// </summary>
public class SecurityAccessBuiltinHeadlessTests
{
    private static string WriteTemp(string ext, string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"m34_{Guid.NewGuid():N}.{ext}");
        File.WriteAllText(path, content);
        return path;
    }

    private static (string dbc, string suite, string ecu) WriteFixtures()
    {
        var dbc = WriteTemp("dbc", """
            VERSION "1.0";
            NS_ :
            BS_:
            BU_: ECU
            BO_ 256 TestMsg: 8 ECU
             SG_ TestSignal : 0|8@1+ (1,0) [0|255] "V"  ECU
            """);
        var suite = WriteTemp("json", """
            {
              "name": "M34Suite",
              "cases": [
                {
                  "id": "case_1",
                  "name": "Delay",
                  "steps": [
                    { "parameters": { "$kind": "delay", "milliseconds": 10 } }
                  ]
                }
              ],
              "globalCaseFixtureKeys": [],
              "suiteFixtureKeys": [],
              "config": { "failurePolicy": "ContinueAll", "continueAfterSetupFailure": true },
              "timeoutMs": 0
            }
            """);
        // 有状态 ECU 脚本（tester 视角 canIds）；0x27 权威响应来自 StatefulVirtualEcu
        // 默认 SecurityAccessServer（XOR-0xAA），脚本转移仅保留观察副作用
        var ecu = WriteTemp("json", """
            {
              "name": "M34Ecu",
              "canIds": { "requestId": "0x7E0", "responseId": "0x7E8" },
              "states": [
                {
                  "name": "default",
                  "transitions": [
                    { "serviceId": "0x27", "subFunction": 1, "response": { "$type": "static", "data": [103, 1, 17, 34, 51, 68] }, "toState": "seedSent" },
                    { "serviceId": "0x27", "subFunction": 2, "response": { "$type": "static", "data": [103, 2] }, "toState": "unlocked" }
                  ]
                },
                {
                  "name": "seedSent",
                  "transitions": [
                    { "serviceId": "0x27", "subFunction": 2, "response": { "$type": "static", "data": [103, 2] }, "toState": "unlocked" }
                  ]
                },
                {
                  "name": "unlocked",
                  "transitions": []
                }
              ]
            }
            """);
        return (dbc, suite, ecu);
    }

    [Fact]
    public async Task KeyAlgorithm_Builtin_CompletesHandshake_WithVirtualEcu()
    {
        var (dbc, suite, ecuPath) = WriteFixtures();
        try
        {
            var cli = new CliArgs(dbc, suite, UdsRequestId: 0x7E0, UdsResponseId: 0x7E8, EcuScriptPath: ecuPath, KeyAlgorithm: "builtin");
            using var host = HeadlessHostBuilder.Build(cli);

            var channel = host.Services.GetRequiredService<ICanChannel>();
            var client = host.Services.GetRequiredService<UdsClient>();
            // 懒注册桥：解析即订阅 channel.FrameReceived → isoTp.ProcessFrame（无此步 client 永远收不到帧）
            _ = host.Services.GetRequiredService<HilIsoTpBridge>();
            // host 在 ECU 模式已自建 StatefulVirtualEcu（急切实例注册），直接解析，勿重复挂载
            var ecu = host.Services.GetRequiredService<StatefulVirtualEcu>();

            await channel.ConnectAsync(BaudRate.Can500kbps, false);

            // 2 参全握手：RequestSeed → XOR-0xAA → SendKey；server 侧权威机解锁
            await client.SecurityAccessAsync(0x01, CancellationToken.None);
            Assert.True(ecu.SecurityServer.IsAuthenticated(1));

            await channel.DisconnectAsync();
        }
        finally
        {
            File.Delete(dbc); File.Delete(suite); File.Delete(ecuPath);
        }
    }

    [Fact]
    public async Task NoKeyAlgorithm_Default_FailsFast_KeyAlgorithmNotConfigured()
    {
        // PlaceholderKeyAlgorithm 默认行为回归：未配算法时 SecurityAccess fail-fast
        var (dbc, suite, ecuPath) = WriteFixtures();
        try
        {
            var cli = new CliArgs(dbc, suite, UdsRequestId: 0x7E0, UdsResponseId: 0x7E8, EcuScriptPath: ecuPath);
            using var host = HeadlessHostBuilder.Build(cli);

            var channel = host.Services.GetRequiredService<ICanChannel>();
            var client = host.Services.GetRequiredService<UdsClient>();
            // 懒注册桥：解析即订阅 channel.FrameReceived → isoTp.ProcessFrame（无此步 client 永远收不到帧）
            _ = host.Services.GetRequiredService<HilIsoTpBridge>();
            var ecu = host.Services.GetRequiredService<StatefulVirtualEcu>();

            await channel.ConnectAsync(BaudRate.Can500kbps, false);

            // 默认 fail-fast：2 参全握手在算法为空时线上之前抛 InvalidOperationException
            //（占位语义回归：不发帧、server 不解锁、不静默用错算法）
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.SecurityAccessAsync(0x01, CancellationToken.None));
            Assert.Contains("IKeyDerivationAlgorithm", ex.Message);
            Assert.False(ecu.SecurityServer.IsAuthenticated(1));

            await channel.DisconnectAsync();
        }
        finally
        {
            File.Delete(dbc); File.Delete(suite); File.Delete(ecuPath);
        }
    }
}
