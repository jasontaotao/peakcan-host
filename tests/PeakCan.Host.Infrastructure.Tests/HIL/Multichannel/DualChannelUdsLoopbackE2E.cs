using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.HIL.StepExecutor;
using PeakCan.HIL.Core.Uds;
using PeakCan.HIL.Core.Uds.IsoTp;
using PeakCan.Host.Infrastructure.CanChannels;
using PeakCan.Host.Infrastructure.HIL;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests.HIL.Multichannel;

/// <summary>
/// §2.5 双通道 UDS 集成测试（Task 12，spec 2026-08-27）：
/// bus-a 挂虚拟 ECU-A（DID F190=AAA），bus-b 挂 ECU-B（F190=BBB），
/// 同 case 内分别 ReadDid 且互不串扰。
/// 每通道独立 UDS 栈（独立 IsoTp 过滤 ID + 独立 UdsClient），经 UdsSessionResolver 按 TargetChannel 路由；
/// 若路由串到对侧通道，bus-b 的 ReadDid Message 会读到 AAAAAA 而非 BBBBBB → 断言捕获。
/// 半 e2e——直构造 resolver + executor + TestSuiteEngine（同 DualChannelLoopbackE2E 模式，
/// 多通道路径无 ICanChannel 注入点）。
/// </summary>
public sealed class DualChannelUdsLoopbackE2E
{
    private const uint RequestIdA = 0x7E0, ResponseIdA = 0x7E8;   // bus-a UDS ID
    private const uint RequestIdB = 0x6E0, ResponseIdB = 0x6E8;   // bus-b UDS ID（与 A 不冲突）

    private sealed class StubAssertionContext : IAssertionContext, IStepVariableStore
    {
        public IDictionary<string, object> Variables { get; } = new Dictionary<string, object>();
        public IDisposable SubscribeDecodedFrames(Action<DecodedFrame> onFrame) => throw new NotSupportedException();
        public double? GetSignalValue(string signalName, int maxAgeMs = 5000) => throw new NotSupportedException();
        public double CurrentTimestamp => throw new NotSupportedException();
        public ValueTask<Result<Unit>> SendFrameAsync(CanFrame frame, CancellationToken ct) => throw new NotSupportedException();
        public IReadOnlyList<DecodedFrame> GetRecentDecodedFrames() => throw new NotSupportedException();
    }

    private static EcuStateTransition ReadDidRule(byte[] response) => new()
    {
        FromState = null,   // wildcard: 匹配任意状态
        ServiceId = 0x22,   // ReadDataByIdentifier
        Response = new StaticResponse(response),
    };

    /// <summary>搭单通道 UDS 栈：VirtualChannel + 虚拟 ECU + host IsoTpLayer + UdsClient + adapter。
    /// ECU 视角 CanIdConfig 与 host 互换（同 UdsStepExecutorTests.BuildUdsAsync 反转逻辑）。</summary>
    private static async Task<(IUdsSession Session, VirtualChannel Channel)> BuildChannelUdsAsync(
        uint reqId, uint respId, params EcuStateTransition[] transitions)
    {
        var channel = new VirtualChannel();
        var hostConfig = new CanIdConfig { RequestId = reqId, ResponseId = respId, IsExtendedFrame = false };
        var ecuConfig = new CanIdConfig { RequestId = respId, ResponseId = reqId, IsExtendedFrame = false };
        var ecu = new StatefulVirtualEcu(channel, ecuConfig, new EcuStateMachine(transitions));
        var isoTp = new IsoTpLayer(hostConfig, async frame => { await channel.WriteAsync(frame).ConfigureAwait(false); });
        channel.FrameReceived += f => isoTp.ProcessFrame(f);   // 仅重组 ECU 响应（按 ResponseId 过滤）
        var uds = new UdsClient(isoTp);
        await channel.ConnectAsync(BaudRate.Can500kbps, false);
        return (new UdsSessionAdapter(uds), channel);
    }

    [Fact]
    public async Task DualChannel_ReadDid_EachChannelGetsOwnEcuData()
    {
        // Arrange：ECU-A 返回 AAA，ECU-B 返回 BBB（同 DID F190，故意不同值以检测串扰）
        var (sessionA, chA) = await BuildChannelUdsAsync(RequestIdA, ResponseIdA,
            ReadDidRule(new byte[] { 0x62, 0xF1, 0x90, 0xAA, 0xAA, 0xAA }));
        var (sessionB, chB) = await BuildChannelUdsAsync(RequestIdB, ResponseIdB,
            ReadDidRule(new byte[] { 0x62, 0xF1, 0x90, 0xBB, 0xBB, 0xBB }));
        try
        {
            var resolver = new UdsSessionResolver(
                new Dictionary<string, IUdsSession>(StringComparer.Ordinal)
                {
                    ["bus-a"] = sessionA,
                    ["bus-b"] = sessionB,
                },
                () => sessionA);   // 默认栈 = bus-a（单通道零回归语义）

            var engine = new TestSuiteEngine(
                new HeadlessFixtureResolver(),
                new IStepExecutor[] { new ReadDidStepExecutor(resolver) });

            var suite = new TestSuite(
                Name: "DualChannelUdsE2E",
                Cases: new[]
                {
                    new TestCase("c1", "ReadBothEcus", "", null,
                        new[]
                        {
                            TestCaseStep.Create(new ReadDidStep(0xF190) { TargetChannel = "bus-a" }),
                            TestCaseStep.Create(new ReadDidStep(0xF190) { TargetChannel = "bus-b" }),
                        },
                        null, Array.Empty<string>()),
                },
                GlobalCaseFixtureKeys: Array.Empty<string>(),
                SuiteFixtureKeys: Array.Empty<string>(),
                Config: new TestSuiteConfig());

            // Act（ctx 供 ReadDid 写 IStepVariableStore；case 边界 engine 清空 Variables）
            var ctx = new StubAssertionContext();
            var result = await engine.ExecuteAsync(suite, ctx, new TestSuiteConfig());

            // Assert：两步都 Passed，且各自读到自己 ECU 的数据（互不串扰）
            var steps = result.CaseResults.Single().StepResults;
            Assert.True(result.AllPassed,
                $"Expected both steps passed, got {result.PassedCases}/{result.TotalCases}. " +
                $"Failures: {string.Join("; ", steps.Where(s => !s.Passed).Select(s => s.Message))}");

            Assert.Equal("bus-a", steps[0].Channel);
            Assert.Contains("AAAAAA", steps[0].Message);   // Read DID 0xF190: AAAAAA（ECU-A）
            Assert.Equal("bus-b", steps[1].Channel);
            Assert.Contains("BBBBBB", steps[1].Message);   // Read DID 0xF190: BBBBBB（ECU-B）

            // 步骤间变量（同 case 共享 store）：bus-b 后执行 → 最终 did_0xF190 = [0xBB]*3
            var didVar = Assert.IsType<byte[]>(ctx.Variables["did_0xF190"]);
            Assert.Equal(new byte[] { 0xBB, 0xBB, 0xBB }, didVar);
        }
        finally
        {
            await chA.DisposeAsync();
            await chB.DisposeAsync();
        }
    }
}
