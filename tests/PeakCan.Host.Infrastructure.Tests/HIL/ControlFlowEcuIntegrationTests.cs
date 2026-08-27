using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.HIL.StepExecutor;
using PeakCan.HIL.Core.Uds;
using PeakCan.HIL.Core.Uds.IsoTp;
using PeakCan.Host.Infrastructure.CanChannels;
using PeakCan.Host.Infrastructure.HIL;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests.HIL;

/// <summary>
/// D.3 spec §12.4 CI 端到端：control-flow suite（If→Repeat→Assign）
/// 对 StatefulVirtualEcu 跑通，验证产出 Path/Iteration。
/// control-flow + Path/Iteration 的单步覆盖见 TestSuiteEngineInterpreterTests（B.2，mock executor）；
/// 本 test 增量 = 真实 ECU（StatefulVirtualEcu DID 响应）端到端。
/// </summary>
public class ControlFlowEcuIntegrationTests
{
    /// <summary>Task B 第二步（spec 2026-08-27 §Q1）：executor 吃 resolver，默认分支回落该 session。</summary>
    private static UdsSessionResolver Resolver(IUdsSession session)
        => new UdsSessionResolver(new Dictionary<string, IUdsSession>(StringComparer.Ordinal), () => session);
    private const int RequestId = 0x7E0;  // host 发请求
    private const int ResponseId = 0x7E8; // ECU 发响应

    /// <summary>
    /// IAssertionContext + IStepVariableStore + IHasRecentFrames：Assign/ReadDid 读写变量；
    /// readDid 经 UdsClient（不经 ctx.SendFrameAsync）；帧集合空（control-flow 不依赖帧）。
    /// </summary>
    private sealed class VarStoreAssertionContext : IAssertionContext, IStepVariableStore, IHasRecentFrames
    {
        public IDictionary<string, object> Variables { get; } = new Dictionary<string, object>();
        public IDisposable SubscribeDecodedFrames(Action<DecodedFrame> onFrame) => new NopDisposable();
        public double? GetSignalValue(string signalName, int maxAgeMs = 5000) => null;
        public double CurrentTimestamp => 0;
        public ValueTask<Result<Unit>> SendFrameAsync(CanFrame frame, CancellationToken ct)
            => ValueTask.FromResult(Result<Unit>.Ok(default));
        public IReadOnlyList<DecodedFrame> GetRecentDecodedFrames() => Array.Empty<DecodedFrame>();
        public IReadOnlyList<CanFrame> GetRecentFrames() => Array.Empty<CanFrame>();
        private sealed class NopDisposable : IDisposable { public void Dispose() { } }
    }

    private static EcuStateTransition Rule(byte sid, byte[] response, byte? subFunction = null) => new()
    {
        FromState = null,  // wildcard: 匹配任意状态
        ServiceId = sid,
        SubFunction = subFunction,
        Response = new StaticResponse(response),
    };

    /// <summary>搭 VirtualChannel + StatefulVirtualEcu + UdsClient 真实 UDS 环回（模式同 UdsStepExecutorTests）。</summary>
    private static async Task<(VirtualChannel Channel, UdsClient Uds)> BuildUdsAsync(
        params EcuStateTransition[] transitions)
    {
        var channel = new VirtualChannel();
        // host 侧 IsoTpLayer: 发请求 0x7E0，收响应 0x7E8
        var hostConfig = new CanIdConfig { RequestId = RequestId, ResponseId = ResponseId, IsExtendedFrame = false };
        // ECU 侧反转（同 EcuScriptLoader.cs 反转逻辑）
        var ecuConfig = new CanIdConfig { RequestId = ResponseId, ResponseId = RequestId, IsExtendedFrame = false };
        var sm = new EcuStateMachine(transitions);
        // StatefulVirtualEcu 订阅 channel.FrameReceived → channel 持有引用，不会被 GC
        var ecu = new StatefulVirtualEcu(channel, ecuConfig, sm);
        var isoTp = new IsoTpLayer(hostConfig, async frame => { await channel.WriteAsync(frame).ConfigureAwait(false); });
        // 桥接响应帧到 IsoTpLayer（生产里由 HilIsoTpBridge 承担）
        channel.FrameReceived += f => isoTp.ProcessFrame(f);
        var uds = new UdsClient(isoTp);
        await channel.ConnectAsync(BaudRate.Can500kbps, false);
        return (channel, uds);
    }

    [Fact]
    public async Task ControlFlow_IfRepeatAssign_AgainstStatefulVirtualEcu_ProducesPathAndIteration()
    {
        // StatefulVirtualEcu 响应 DID 0xF190: 0x62(positive SID) 0xF1 0x90(DID) 0xAA 0xBB(data)
        var (channel, uds) = await BuildUdsAsync(
            Rule(0x22, new byte[] { 0x62, 0xF1, 0x90, 0xAA, 0xBB }));
        try
        {
            var readDidExec = new ReadDidStepExecutor(Resolver(new UdsSessionAdapter(uds)));
            var engine = new TestSuiteEngine(
                new HeadlessFixtureResolver(),
                new IStepExecutor[] { readDidExec });

            // control-flow suite (spec §12.4 If→Repeat→Assign):
            // If body = [readDid(DID 0xF190), Repeat(Fixed 2, body=[Assign])]
            var readDidStep = TestCaseStep.Create(new ReadDidStep(0xF190, "vin"));
            var assignStep = TestCaseStep.Create(new AssignStep("flag", "1"));
            var repeatStep = TestCaseStep.Create(new RepeatStep(
                RepeatMode.Fixed, Count: "2", Condition: null,
                Body: new[] { assignStep }, MaxIterations: "100"));
            var ifStep = TestCaseStep.Create(new IfStep("1 == 1", new[] { readDidStep, repeatStep }, null));
            var suite = new TestSuite(
                "ControlFlowE2E",
                new[]
                {
                    new TestCase("cf1", "Control Flow E2E", "", null,
                        new[] { ifStep }, null, Array.Empty<string>()),
                },
                Array.Empty<string>(), Array.Empty<string>(), new TestSuiteConfig());

            var result = await engine.ExecuteAsync(suite, new VarStoreAssertionContext(), new TestSuiteConfig());
            var steps = result.CaseResults[0].StepResults;

            // readDid: Passed (StatefulVirtualEcu 响应 DID 0xF190), Path="0.0" (If body index 0)
            var readDidResult = steps.First(s => s.Kind == TestCaseStepKind.ReadDid);
            readDidResult.Status.Should().Be(StepStatus.Passed, "StatefulVirtualEcu 响应 DID 0xF190");
            readDidResult.Path.Should().Be("0.0", "If body 内 readDid Path = 父容器 StepIndex.body序号");

            // If 容器: Path=null (顶层容器)
            var ifContainer = steps.First(s => s.Kind == TestCaseStepKind.If);
            ifContainer.Path.Should().BeNull("顶层容器 Path=null（向后兼容旧 JSON）");

            // Repeat body Assign: Iteration 0/1, Path="0.1.0" (If body index 1 = Repeat, Repeat body index 0 = Assign)
            var assignResults = steps.Where(s => s.Kind == TestCaseStepKind.Assign).ToList();
            assignResults.Should().HaveCount(2, "Repeat Count=2 跑 2 次");
            assignResults[0].Iteration.Should().Be(0);
            assignResults[1].Iteration.Should().Be(1);
            assignResults[0].Path.Should().Be("0.1.0",
                "If(index 0) body[1]=Repeat → Repeat body[0]=Assign → Path=0.1.0");
        }
        finally { await channel.DisposeAsync(); }
    }
}
