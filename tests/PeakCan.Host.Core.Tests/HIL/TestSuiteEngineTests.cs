using System.Threading.Channels;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.HIL.StepExecutor;
using PeakCan.HIL.Core.Tests.HIL.Fakes;
using PeakCan.HIL.Core.Uds;
using PeakCan.HIL.Core.Uds.IsoTp;
using Xunit;

namespace PeakCan.HIL.Core.Tests.HIL;

public class TestSuiteEngineTests
{
    private static TestSuiteEngine CreateEngine(params IStepExecutor[] executors)
    {
        var fixtureResolver = new FakeFixtureResolver();
        return new TestSuiteEngine(fixtureResolver, executors);
    }

    private static TestCase CreateCase(params TestCaseStep[] steps) => new(
        Id: "case_1", Name: "Test Case", Description: "",
        PreConditions: null, Steps: steps, PostConditions: null,
        Tags: Array.Empty<string>(), TimeoutMs: 0, CaseFixtureKeys: null);

    [Fact]
    public async Task EmptySuite_Returns_TotalCasesZero_AllPassedFalse()
    {
        var engine = CreateEngine();
        var suite = new TestSuite("Empty", Array.Empty<TestCase>(),
            Array.Empty<string>(), Array.Empty<string>(), new TestSuiteConfig(), 0);

        var result = await engine.ExecuteAsync(suite, new FakeAssertionContext(), new TestSuiteConfig(), default, default);

        Assert.Equal(0, result.TotalCases);
        Assert.False(result.AllPassed);
    }

    [Fact]
    public async Task SingleCase_SinglePassedStep_ReturnsPassed()
    {
        var exec = new FakeStepExecutor(TestCaseStepKind.Comment)
        {
            Result = new StepResult(0, TestCaseStepKind.Comment, null, StepStatus.Passed, "ok", null, null, 0),
        };
        var engine = CreateEngine(exec);
        var suite = new TestSuite("S", new[] { CreateCase(TestCaseStep.Create(new CommentStep("doc"))) },
            Array.Empty<string>(), Array.Empty<string>(), new TestSuiteConfig(), 0);

        var result = await engine.ExecuteAsync(suite, new FakeAssertionContext(), new TestSuiteConfig(), default, default);

        Assert.Equal(1, result.PassedCases);
        Assert.True(result.AllPassed);
    }

    [Fact]
    public async Task CommentStep_Only_ReturnsPassed()
    {
        var engine = CreateEngine();
        var suite = new TestSuite("S", new[] { CreateCase(TestCaseStep.Create(new CommentStep("doc"))) },
            Array.Empty<string>(), Array.Empty<string>(), new TestSuiteConfig(), 0);

        var result = await engine.ExecuteAsync(suite, new FakeAssertionContext(), new TestSuiteConfig(), default, default);

        Assert.True(result.AllPassed);
    }

    [Fact]
    public async Task SingleCase_SingleFailedStep_ReturnsFailed()
    {
        var exec = new FakeStepExecutor(TestCaseStepKind.AssertSignal)
        {
            Result = new StepResult(0, TestCaseStepKind.AssertSignal, null, StepStatus.Failed, "fail", null, null, 0),
        };
        var engine = CreateEngine(exec);
        var step = TestCaseStep.Create(new AssertSignalStep("RPM", 3000.0, 10.0));
        var suite = new TestSuite("S", new[] { CreateCase(step) },
            Array.Empty<string>(), Array.Empty<string>(), new TestSuiteConfig(), 0);

        var result = await engine.ExecuteAsync(suite, new FakeAssertionContext(), new TestSuiteConfig(), default, default);

        Assert.Equal(1, result.FailedCases);
        Assert.False(result.AllPassed);
    }

    [Fact]
    public async Task StopCaseOnFailure_StepFails_RemainingStepsSkipped()
    {
        var exec = new FakeStepExecutor(TestCaseStepKind.AssertSignal)
        {
            Result = new StepResult(0, TestCaseStepKind.AssertSignal, null, StepStatus.Failed, "fail", null, null, 0),
        };
        var engine = CreateEngine(exec);
        var step1 = TestCaseStep.Create(new AssertSignalStep("RPM", 3000.0, 10.0));
        var step2 = TestCaseStep.Create(new CommentStep("should skip"));
        var suite = new TestSuite("S", new[] { CreateCase(step1, step2) },
            Array.Empty<string>(), Array.Empty<string>(), new TestSuiteConfig(FailurePolicy.StopCaseOnFailure), 0);

        var result = await engine.ExecuteAsync(suite, new FakeAssertionContext(),
            new TestSuiteConfig(FailurePolicy.StopCaseOnFailure), default, default);

        Assert.Equal(1, result.FailedCases);
        Assert.Equal(StepStatus.Skipped, result.CaseResults[0].StepResults[1].Status);
    }

    [Fact]
    public async Task StepIndex_OverriddenByEngine()
    {
        var exec = new FakeStepExecutor(TestCaseStepKind.Comment);
        var engine = CreateEngine(exec);
        var step = TestCaseStep.Create(new CommentStep("doc"));
        var suite = new TestSuite("S", new[] { CreateCase(step) },
            Array.Empty<string>(), Array.Empty<string>(), new TestSuiteConfig(), 0);

        var result = await engine.ExecuteAsync(suite, new FakeAssertionContext(), new TestSuiteConfig(), default, default);

        Assert.Equal(0, result.CaseResults[0].StepResults[0].StepIndex);
    }

    [Fact]
    public async Task TotalSteps_ExcludesCommentSteps()
    {
        var exec = new FakeStepExecutor(TestCaseStepKind.AssertSignal)
        {
            Result = new StepResult(0, TestCaseStepKind.AssertSignal, null, StepStatus.Passed, "ok", null, null, 0),
        };
        var engine = CreateEngine(exec);
        var step1 = TestCaseStep.Create(new AssertSignalStep("RPM", 3000.0, 10.0));
        var step2 = TestCaseStep.Create(new CommentStep("doc"));
        var suite = new TestSuite("S", new[] { CreateCase(step1, step2) },
            Array.Empty<string>(), Array.Empty<string>(), new TestSuiteConfig(), 0);

        var result = await engine.ExecuteAsync(suite, new FakeAssertionContext(), new TestSuiteConfig(), default, default);

        Assert.Equal(1, result.CaseResults[0].TotalSteps); // Excludes Comment
        Assert.Equal(1, result.CaseResults[0].CommentSteps);
    }

    // ── 负测试判定真值表（ExpectedVerdict）──
    // 场景 1：默认 Any + 步骤 Failed → case Failed（行为不变，负测试不生效）

    [Fact]
    public async Task ExpectedVerdictAny_StepFails_CaseFails()
    {
        var exec = new FakeStepExecutor(TestCaseStepKind.AssertSignal)
        {
            Result = new StepResult(0, TestCaseStepKind.AssertSignal, null, StepStatus.Failed, "fail", null, null, 0),
        };
        var engine = CreateEngine(exec);
        var step = TestCaseStep.Create(new AssertSignalStep("RPM", 3000.0, 10.0), expectedVerdict: ExpectedVerdict.Any);
        var suite = new TestSuite("S", new[] { CreateCase(step) },
            Array.Empty<string>(), Array.Empty<string>(), new TestSuiteConfig(), 0);

        var result = await engine.ExecuteAsync(suite, new FakeAssertionContext(), new TestSuiteConfig(), default, default);

        Assert.Equal(1, result.FailedCases);
        Assert.False(result.AllPassed);
        var stepResult = result.CaseResults[0].StepResults[0];
        Assert.Equal(StepStatus.Failed, stepResult.Status);
        Assert.False(stepResult.WasNegatedTest);
    }

    // 场景 2：预期 Fail + 实际 Failed → 负测试通过，步骤提升为 Passed（WasNegatedTest=true），case Passed

    [Fact]
    public async Task ExpectedVerdictFail_StepFails_NegatedTestPasses_CasePasses()
    {
        var exec = new FakeStepExecutor(TestCaseStepKind.AssertSignal)
        {
            Result = new StepResult(0, TestCaseStepKind.AssertSignal, null, StepStatus.Failed, "fail", null, null, 0),
        };
        var engine = CreateEngine(exec);
        var step = TestCaseStep.Create(new AssertSignalStep("RPM", 3000.0, 10.0), expectedVerdict: ExpectedVerdict.Fail);
        var suite = new TestSuite("S", new[] { CreateCase(step) },
            Array.Empty<string>(), Array.Empty<string>(), new TestSuiteConfig(), 0);

        var result = await engine.ExecuteAsync(suite, new FakeAssertionContext(), new TestSuiteConfig(), default, default);

        Assert.Equal(1, result.PassedCases);
        Assert.True(result.AllPassed);
        var stepResult = result.CaseResults[0].StepResults[0];
        // StepResult 是位置 record：WasNegatedTest 参与合成值相等，故按单属性断言（ledger finding #3）
        Assert.Equal(StepStatus.Passed, stepResult.Status);
        Assert.True(stepResult.WasNegatedTest);
        Assert.Contains("failed as expected (negated test)", stepResult.Message);
    }

    // 场景 3：预期 Fail + 实际 Passed → 负测试未生效（如发错误请求却收到成功响应），强制 Failed，case Failed

    [Fact]
    public async Task ExpectedVerdictFail_StepPasses_NegatedTestDidNotTakeEffect_CaseFails()
    {
        var exec = new FakeStepExecutor(TestCaseStepKind.AssertSignal)
        {
            Result = new StepResult(0, TestCaseStepKind.AssertSignal, null, StepStatus.Passed, "ok", null, null, 0),
        };
        var engine = CreateEngine(exec);
        var step = TestCaseStep.Create(new AssertSignalStep("RPM", 3000.0, 10.0), expectedVerdict: ExpectedVerdict.Fail);
        var suite = new TestSuite("S", new[] { CreateCase(step) },
            Array.Empty<string>(), Array.Empty<string>(), new TestSuiteConfig(), 0);

        var result = await engine.ExecuteAsync(suite, new FakeAssertionContext(), new TestSuiteConfig(), default, default);

        Assert.Equal(1, result.FailedCases);
        Assert.False(result.AllPassed);
        var stepResult = result.CaseResults[0].StepResults[0];
        Assert.Equal(StepStatus.Failed, stepResult.Status);
        Assert.False(stepResult.WasNegatedTest);
        Assert.Contains("expected failure but passed (negated test)", stepResult.Message);
    }

    // 场景 4：预期 Fail + 实际 Failed + StopCaseOnFailure → 提升后 Passed==true，后续步骤不被跳过

    [Fact]
    public async Task ExpectedVerdictFail_StepFails_StopCaseOnFailure_DoesNotSkipSubsequentSteps()
    {
        var exec = new FakeStepExecutor(TestCaseStepKind.AssertSignal)
        {
            Result = new StepResult(0, TestCaseStepKind.AssertSignal, null, StepStatus.Failed, "fail", null, null, 0),
        };
        var engine = CreateEngine(exec);
        var step1 = TestCaseStep.Create(new AssertSignalStep("RPM", 3000.0, 10.0), expectedVerdict: ExpectedVerdict.Fail);
        var step2 = TestCaseStep.Create(new CommentStep("should still run"));
        var suite = new TestSuite("S", new[] { CreateCase(step1, step2) },
            Array.Empty<string>(), Array.Empty<string>(), new TestSuiteConfig(FailurePolicy.StopCaseOnFailure), 0);

        var result = await engine.ExecuteAsync(suite, new FakeAssertionContext(),
            new TestSuiteConfig(FailurePolicy.StopCaseOnFailure), default, default);

        Assert.Equal(1, result.PassedCases);
        Assert.True(result.AllPassed);
        var stepResults = result.CaseResults[0].StepResults;
        Assert.Equal(StepStatus.Passed, stepResults[0].Status);
        Assert.True(stepResults[0].WasNegatedTest);
        Assert.Equal(StepStatus.Comment, stepResults[1].Status); // NOT Skipped
    }

    // 场景 5：预期 Fail + 无对应执行器（配置错误）→ 步骤从未执行，必须保持 Failed，case 失败。
    // 防止"引擎合成的 No executor 失败"被负测试判定提升为 Passed 造成假绿（review finding）

    [Fact]
    public async Task ExpectedVerdictFail_NoExecutorForKind_StepStaysFailed_CaseFails()
    {
        var engine = CreateEngine(); // 不注册任何执行器
        var step = TestCaseStep.Create(new AssertSignalStep("RPM", 3000.0, 10.0), expectedVerdict: ExpectedVerdict.Fail);
        var suite = new TestSuite("S", new[] { CreateCase(step) },
            Array.Empty<string>(), Array.Empty<string>(), new TestSuiteConfig(), 0);

        var result = await engine.ExecuteAsync(suite, new FakeAssertionContext(), new TestSuiteConfig(), default, default);

        Assert.Equal(1, result.FailedCases);
        Assert.False(result.AllPassed);
        var stepResult = result.CaseResults[0].StepResults[0];
        Assert.Equal(StepStatus.Failed, stepResult.Status);
        Assert.False(stepResult.WasNegatedTest);
        Assert.Contains("No executor for kind", stepResult.Message);
    }

    // 场景 6：预期 Fail + 执行器抛异常（传输层爆炸等）→ 步骤从未执行，保持 Failed，case 失败

    [Fact]
    public async Task ExpectedVerdictFail_ExecutorThrows_StepStaysFailed_CaseFails()
    {
        var exec = new FakeStepExecutor(TestCaseStepKind.AssertSignal)
        {
            ExceptionToThrow = new InvalidOperationException("transport boom"),
        };
        var engine = CreateEngine(exec);
        var step = TestCaseStep.Create(new AssertSignalStep("RPM", 3000.0, 10.0), expectedVerdict: ExpectedVerdict.Fail);
        var suite = new TestSuite("S", new[] { CreateCase(step) },
            Array.Empty<string>(), Array.Empty<string>(), new TestSuiteConfig(), 0);

        var result = await engine.ExecuteAsync(suite, new FakeAssertionContext(), new TestSuiteConfig(), default, default);

        Assert.Equal(1, result.FailedCases);
        Assert.False(result.AllPassed);
        var stepResult = result.CaseResults[0].StepResults[0];
        Assert.Equal(StepStatus.Failed, stepResult.Status);
        Assert.False(stepResult.WasNegatedTest);
        Assert.Contains("Executor threw: transport boom", stepResult.Message);
    }

    // ── 引擎端到端：ReadDid → AssertVariable 链（Task 2.10）──
    // 最小内存 CAN 总线（LoopbackBus）等价 Infrastructure 的 VirtualChannel +
    // EcuStateMachine 响应器等价 StatefulVirtualEcu：主机侧 IsoTpLayer/UdsClient
    // 发出 ReadDID 请求，ECU 侧按规则回正响应，ReadDidStepExecutor 把数据写入
    // IStepVariableStore，随后 AssertVariableStepExecutor 读到并断言通过。
    // 全程经真实 TestSuiteEngine.ExecuteAsync，同一 context 对象流经两个步骤。

    private const uint E2eRequestId = 0x7E0;
    private const uint E2eResponseId = 0x7E8;

    /// <summary>实现 IAssertionContext + IStepVariableStore；IAssertionContext 其余成员不用即抛。</summary>
    private sealed class StoreAssertionContext : IAssertionContext, IStepVariableStore
    {
        public IDictionary<string, object> Variables { get; } = new Dictionary<string, object>();
        public IDisposable SubscribeDecodedFrames(Action<DecodedFrame> onFrame) => throw new NotSupportedException();
        public double? GetSignalValue(string signalName, int maxAgeMs = 5000) => throw new NotSupportedException();
        public double CurrentTimestamp => throw new NotSupportedException();
        public ValueTask<Result<Unit>> SendFrameAsync(CanFrame frame, CancellationToken ct) => throw new NotSupportedException();
        public IReadOnlyList<DecodedFrame> GetRecentDecodedFrames() => throw new NotSupportedException();
    }

    /// <summary>内存帧总线：写帧异步派发给订阅者（等价 VirtualChannel.ConsumerLoop）。</summary>
    private sealed class LoopbackBus : IDisposable
    {
        private readonly Channel<CanFrame> _frames = Channel.CreateUnbounded<CanFrame>();
        private readonly CancellationTokenSource _cts = new();
        private Action<CanFrame>? _subscribers;

        public LoopbackBus()
            => _ = Task.Run(() => ConsumerLoopAsync(_cts.Token));

        public void Publish(CanFrame frame) => _frames.Writer.TryWrite(frame);

        public void Subscribe(Action<CanFrame> onFrame) => _subscribers += onFrame;

        private async Task ConsumerLoopAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var frame in _frames.Reader.ReadAllAsync(ct))
                {
                    var handler = _subscribers;
                    if (handler is null) continue;
                    foreach (var sub in handler.GetInvocationList())
                    {
                        try { ((Action<CanFrame>)sub)(frame); }
                        catch { /* 单订阅者异常不拖垮总线（同 VirtualChannel） */ }
                    }
                }
            }
            catch (OperationCanceledException) { /* 正常关闭 */ }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _frames.Writer.TryComplete();
        }
    }

    private static EcuStateTransition E2eRule(byte sid, byte[] response) => new()
    {
        FromState = null, // wildcard: 匹配任意状态
        ServiceId = sid,
        Response = new StaticResponse(response),
    };

    private static async Task SendEcuResponseAsync(IsoTpLayer ecuIsoTp, byte[] response, int delayMs)
    {
        if (delayMs > 0) await Task.Delay(delayMs).ConfigureAwait(false);
        await ecuIsoTp.SendMessageAsync(response).ConfigureAwait(false);
    }

    /// <summary>搭真实 UDS 环回（等价 UdsStepExecutorTests.BuildUdsAsync，仅 Core 可见类型）。</summary>
    private static (UdsClient Uds, LoopbackBus Bus) BuildE2eLoopback(params EcuStateTransition[] transitions)
    {
        var bus = new LoopbackBus();
        // host 侧 IsoTpLayer：发请求 0x7E0，收响应 0x7E8
        var hostConfig = new CanIdConfig { RequestId = E2eRequestId, ResponseId = E2eResponseId, IsExtendedFrame = false };
        // ECU 侧用 ECU 视角：内部 IsoTpLayer txCanId=config.RequestId（发响应）、
        // ProcessFrame 过滤 config.ResponseId（收请求）→ 与 host 互换
        var ecuConfig = new CanIdConfig { RequestId = E2eResponseId, ResponseId = E2eRequestId, IsExtendedFrame = false };

        var sm = new EcuStateMachine(transitions);
        var ecuIsoTp = new IsoTpLayer(ecuConfig, frame => { bus.Publish(frame); return Task.CompletedTask; });
        bus.Subscribe(f => ecuIsoTp.ProcessFrame(f));

        var hostIsoTp = new IsoTpLayer(hostConfig, frame => { bus.Publish(frame); return Task.CompletedTask; });
        bus.Subscribe(f => hostIsoTp.ProcessFrame(f));

        // ECU 响应器（等价 StatefulVirtualEcu.OnUdsRequestReceived → SendResponseAsync）
        ecuIsoTp.MessageReceived += req =>
        {
            var (response, delayMs) = sm.ProcessRequest(req);
            _ = SendEcuResponseAsync(ecuIsoTp, response, delayMs);
        };

        // P2 放宽到 1s：循环回环下响应毫秒级到达，防止慢 CI 上撞默认 50ms 超时
        var uds = new UdsClient(hostIsoTp, new UdsTimer { P2Timeout = TimeSpan.FromSeconds(1) });
        return (uds, bus);
    }

    [Fact]
    public async Task ReadDid_ThenAssertVariable_EndToEnd_Passes()
    {
        var (uds, bus) = BuildE2eLoopback(
            E2eRule(0x22, new byte[] { 0x62, 0xF1, 0x90, 0xAA, 0xBB }));
        try
        {
            var engine = CreateEngine(new ReadDidStepExecutor(uds), new AssertVariableStepExecutor());
            var step1 = TestCaseStep.Create(new ReadDidStep(0xF190));
            var step2 = TestCaseStep.Create(new AssertVariableStep(
                "did_0xF190", ExpectedHexBytes: new byte[] { 0xAA, 0xBB }, TimeoutMs: 200));
            var suite = new TestSuite("E2E", new[] { CreateCase(step1, step2) },
                Array.Empty<string>(), Array.Empty<string>(), new TestSuiteConfig(), 0);

            var result = await engine.ExecuteAsync(suite, new StoreAssertionContext(), new TestSuiteConfig(), default, default);

            Assert.Equal(1, result.PassedCases);
            Assert.Equal(0, result.FailedCases);
            Assert.True(result.AllPassed);
            var steps = result.CaseResults[0].StepResults;
            Assert.Equal(2, steps.Count);
            Assert.Equal(StepStatus.Passed, steps[0].Status); // ReadDid 真实 UDS 往返
            Assert.Contains("Read DID 0xF190", steps[0].Message);
            Assert.Equal(StepStatus.Passed, steps[1].Status); // AssertVariable 读到链上变量
            Assert.Contains("did_0xF190", steps[1].Message);
            Assert.Contains("matches", steps[1].Message);
        }
        finally { bus.Dispose(); }
    }
}
