using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.HIL.StepExecutor;
using PeakCan.HIL.Core.Uds;
using PeakCan.HIL.Core.Uds.IsoTp;
using PeakCan.Host.Infrastructure.CanChannels;
using PeakCan.Host.Infrastructure.HIL;
using Xunit;

namespace PeakCan.Host.Infrastructure.Tests.HIL.StepExecutor;

/// <summary>
/// Phase A: 6 个 UDS step executor + AssertDidValueStepExecutor 测试。
/// 用 VirtualEcu（EcuStateMachine）+ 手搭 IsoTpLayer/UdsClient 构造真实 UDS 环回，
/// StubAssertionContext 只实现 IStepVariableStore（executor 只用这一通道）。
/// </summary>
public class UdsStepExecutorTests
{
    private const int RequestId = 0x7E0;
    private const int ResponseId = 0x7E8;

    /// <summary>XOR 0xAA 密钥算法（与 OdxSecurityAccessE2ETests 一致）。</summary>
    private sealed class XorKeyAlgorithm : IKeyDerivationAlgorithm
    {
        public byte[] ComputeKey(byte[] seed, byte securityLevel)
            => seed.Select(b => (byte)(b ^ 0xAA)).ToArray();
    }

    /// <summary>只实现 IStepVariableStore；IAssertionContext 其余成员不用即抛。</summary>
    private sealed class StubAssertionContext : IAssertionContext, IStepVariableStore
    {
        public IDictionary<string, object> Variables { get; } = new Dictionary<string, object>();
        public IDisposable SubscribeDecodedFrames(Action<DecodedFrame> onFrame) => throw new NotSupportedException();
        public double? GetSignalValue(string signalName, int maxAgeMs = 5000) => throw new NotSupportedException();
        public double CurrentTimestamp => throw new NotSupportedException();
        public ValueTask<Result<Unit>> SendFrameAsync(CanFrame frame, CancellationToken ct) => throw new NotSupportedException();
        public IReadOnlyList<DecodedFrame> GetRecentDecodedFrames() => throw new NotSupportedException();
    }

    private static EcuStateTransition Rule(byte sid, byte[] response, byte? subFunction = null) => new()
    {
        FromState = null, // wildcard: 匹配任意状态
        ServiceId = sid,
        SubFunction = subFunction,
        Response = new StaticResponse(response),
    };

    private static async Task<(VirtualChannel Channel, UdsClient Uds)> BuildUdsAsync(
        IKeyDerivationAlgorithm? keyAlgorithm, params EcuStateTransition[] transitions)
    {
        var channel = new VirtualChannel();
        // host 侧 IsoTpLayer：发请求 0x7E0，收响应 0x7E8
        var hostConfig = new CanIdConfig { RequestId = RequestId, ResponseId = ResponseId, IsExtendedFrame = false };
        // ECU 侧（StatefulVirtualEcu）用 ECU 视角：内部 IsoTpLayer txCanId=config.RequestId（发响应）、
        // ProcessFrame 过滤 config.ResponseId（收请求）→ 与 host 互换（同 EcuScriptLoader.cs:96-99 的反转逻辑）
        var ecuConfig = new CanIdConfig { RequestId = ResponseId, ResponseId = RequestId, IsExtendedFrame = false };
        var sm = new EcuStateMachine(transitions);
        // StatefulVirtualEcu 构造时订阅 channel.FrameReceived → channel 持有引用，不会被 GC
        var ecu = new StatefulVirtualEcu(channel, ecuConfig, sm);
        var isoTp = new IsoTpLayer(hostConfig, async frame => { await channel.WriteAsync(frame).ConfigureAwait(false); });
        // 桥接响应帧到 IsoTpLayer（生产里由 HilIsoTpBridge 承担）。
        // ProcessFrame 按 ResponseId 过滤，请求帧被忽略，仅 ECU 响应被重组 → 安全。
        channel.FrameReceived += f => isoTp.ProcessFrame(f);
        var uds = keyAlgorithm is null ? new UdsClient(isoTp) : new UdsClient(isoTp, keyAlgorithm);
        await channel.ConnectAsync(BaudRate.Can500kbps, false);
        return (channel, uds);
    }

    // ---- ReadDid ----

    [Fact]
    public async Task ReadDid_WithVirtualEcu_ReturnsData()
    {
        var (channel, uds) = await BuildUdsAsync(null,
            Rule(0x22, new byte[] { 0x62, 0xF1, 0x90, 0xAA, 0xBB }));
        try
        {
            var executor = new ReadDidStepExecutor(uds);
            var result = await executor.ExecuteAsync(
                TestCaseStep.Create(new ReadDidStep(0xF190)), new StubAssertionContext(), default);

            Assert.Equal(StepStatus.Passed, result.Status);
            Assert.Contains("0xF190", result.Message);
        }
        finally { await channel.DisposeAsync(); }
    }

    [Fact]
    public async Task ReadDid_Nrc_ReturnsFailed()
    {
        // 无 0x22 规则 → EcuStateMachine 返回 NRC 0x11 → UdsNegativeResponseException
        var (channel, uds) = await BuildUdsAsync(null);
        try
        {
            var executor = new ReadDidStepExecutor(uds);
            var result = await executor.ExecuteAsync(
                TestCaseStep.Create(new ReadDidStep(0xF190)), new StubAssertionContext(), default);

            Assert.Equal(StepStatus.Failed, result.Status);
            Assert.Contains("ReadDID", result.Message);
        }
        finally { await channel.DisposeAsync(); }
    }

    [Fact]
    public async Task ReadDid_WritesToVariables()
    {
        var (channel, uds) = await BuildUdsAsync(null,
            Rule(0x22, new byte[] { 0x62, 0xF1, 0x90, 0xAA, 0xBB }));
        try
        {
            var ctx = new StubAssertionContext();
            var executor = new ReadDidStepExecutor(uds);
            var result = await executor.ExecuteAsync(
                TestCaseStep.Create(new ReadDidStep(0xF190)), ctx, default);

            Assert.Equal(StepStatus.Passed, result.Status);
            Assert.Equal(new byte[] { 0xAA, 0xBB }, ctx.Variables["did_0xF190"]);
        }
        finally { await channel.DisposeAsync(); }
    }

    // ---- AssertDidValue ----

    [Fact]
    public async Task AssertDidValue_Match_Passes()
    {
        var ctx = new StubAssertionContext();
        ctx.Variables["did_0xF190"] = new byte[] { 0xAA, 0xBB };

        var executor = new AssertDidValueStepExecutor();
        var result = await executor.ExecuteAsync(
            TestCaseStep.Create(new AssertDidValueStep("did_0xF190", new byte[] { 0xAA, 0xBB })),
            ctx, default);

        Assert.Equal(StepStatus.Passed, result.Status);
    }

    [Fact]
    public async Task AssertDidValue_MissingKey_TimesOut()
    {
        var ctx = new StubAssertionContext(); // 无键

        var executor = new AssertDidValueStepExecutor();
        var result = await executor.ExecuteAsync(
            TestCaseStep.Create(new AssertDidValueStep("nokey", null, TimeoutMs: "100")),
            ctx, default);

        Assert.Equal(StepStatus.Failed, result.Status);
        Assert.Contains("not available", result.Message);
    }

    // ---- WriteDid ----

    [Fact]
    public async Task WriteDid_Success()
    {
        var (channel, uds) = await BuildUdsAsync(null,
            Rule(0x2E, new byte[] { 0x6E, 0xF1, 0x90 }));
        try
        {
            var executor = new WriteDidStepExecutor(uds);
            var result = await executor.ExecuteAsync(
                TestCaseStep.Create(new WriteDidStep(0xF190, new byte[] { 0x01, 0x02 })),
                new StubAssertionContext(), default);

            Assert.Equal(StepStatus.Passed, result.Status);
        }
        finally { await channel.DisposeAsync(); }
    }

    // ---- SessionControl ----

    [Fact]
    public async Task SessionControl_ChangesSession()
    {
        // 正响应 [0x50, session, P2hi, P2lo, P2*hi, P2*lo] → P2=50ms, P2*=250ms
        var (channel, uds) = await BuildUdsAsync(null,
            Rule(0x10, new byte[] { 0x50, 0x02, 0x00, 0x32, 0x00, 0xFA }));
        try
        {
            var ctx = new StubAssertionContext();
            var executor = new SessionControlStepExecutor(uds);
            var result = await executor.ExecuteAsync(
                TestCaseStep.Create(new SessionControlStep(0x02)), ctx, default);

            Assert.Equal(StepStatus.Passed, result.Status);
            Assert.Equal(new byte[] { 0x02 }, ctx.Variables["session"]);   // byte[] 统一（M-2）
        }
        finally { await channel.DisposeAsync(); }
    }

    // ---- ClearDtc ----

    [Fact]
    public async Task ClearDtc_Success()
    {
        // UdsClient.OnMessageReceived 要求正响应 ≥2 字节（TransportFlow.cs:165），[0x54] 补 1 字节 padding
        var (channel, uds) = await BuildUdsAsync(null,
            Rule(0x14, new byte[] { 0x54, 0x00 }));
        try
        {
            var executor = new ClearDtcStepExecutor(uds);
            var result = await executor.ExecuteAsync(
                TestCaseStep.Create(new ClearDtcStep()), new StubAssertionContext(), default);

            Assert.Equal(StepStatus.Passed, result.Status);
            Assert.Contains("Cleared all DTCs", result.Message);
        }
        finally { await channel.DisposeAsync(); }
    }

    // ---- RoutineControl ----

    [Fact]
    public async Task RoutineControl_StartRoutine()
    {
        // 正响应 [0x71, type, idHi, idLo, result] → 剥离 SID 后 [0x71→], result[3..]
        var (channel, uds) = await BuildUdsAsync(null,
            Rule(0x31, new byte[] { 0x71, 0x01, 0x02, 0x03, 0xAA }));
        try
        {
            var executor = new RoutineControlStepExecutor(uds);
            var result = await executor.ExecuteAsync(
                TestCaseStep.Create(new RoutineControlStep(1, 0x0203)),
                new StubAssertionContext(), default);

            Assert.Equal(StepStatus.Passed, result.Status);
            Assert.Contains("0x0203", result.Message);
        }
        finally { await channel.DisposeAsync(); }
    }

    // ---- SecurityAccess ----

    [Fact]
    public async Task SecurityAccess_FullHandshake()
    {
        var (channel, uds) = await BuildUdsAsync(new XorKeyAlgorithm(),
            Rule(0x27, new byte[] { 0x67, 0x01, 0x11, 0x22, 0x33, 0x44 }, 0x01),
            Rule(0x27, new byte[] { 0x67, 0x02 }, 0x02));
        try
        {
            var ctx = new StubAssertionContext();
            var executor = new SecurityAccessStepExecutor(uds);
            var result = await executor.ExecuteAsync(
                TestCaseStep.Create(new SecurityAccessStep(1)), ctx, default);

            Assert.Equal(StepStatus.Passed, result.Status);
            Assert.Equal(new byte[] { 0x01 }, ctx.Variables["security_level"]);   // byte[] 统一（M-3）
        }
        finally { await channel.DisposeAsync(); }
    }

    // ---- trace-replay 模式不注册 ----

    [Fact]
    public async Task UdsSteps_NotRegisteredInTraceMode_ReturnsNoExecutor()
    {
        // 无 UDS executor 注册的 TestSuiteEngine（等价 trace-replay 模式）→ 明确失败消息
        var engine = new TestSuiteEngine(
            new HeadlessFixtureResolver(), Array.Empty<IStepExecutor>());
        var suite = new TestSuite(
            "TraceSuite",
            new[]
            {
                new TestCase("c1", "ReadDID", "", null,
                    new[] { TestCaseStep.Create(new ReadDidStep(0xF190)) },
                    null, Array.Empty<string>()),
            },
            Array.Empty<string>(), Array.Empty<string>(), new TestSuiteConfig());

        var result = await engine.ExecuteAsync(suite, new StubAssertionContext(), new TestSuiteConfig());

        Assert.Equal(StepStatus.Failed, result.CaseResults[0].StepResults[0].Status);
        Assert.Contains("No executor for kind ReadDid", result.CaseResults[0].StepResults[0].Message);
    }

    // ---- 跨 case 变量隔离（review M-1）----

    /// <summary>假 Delay executor：写入 Variables["k"]，用于验证引擎在 case 边界清空。</summary>
    private sealed class WriteVarExecutor : IStepExecutor
    {
        public TestCaseStepKind Kind => TestCaseStepKind.Delay;
        public Task<StepResult> ExecuteAsync(TestCaseStep step, IAssertionContext ctx, CancellationToken ct)
        {
            if (ctx is IStepVariableStore s) s.Variables["k"] = new byte[] { 0xAA };
            return Task.FromResult(new StepResult(0, step.Kind, step.Label, StepStatus.Passed, "ok", null, null, 0));
        }
    }

    [Fact]
    public async Task Variables_ClearedBetweenCases()
    {
        var engine = new TestSuiteEngine(
            new HeadlessFixtureResolver(),
            new IStepExecutor[] { new WriteVarExecutor(), new AssertDidValueStepExecutor() });
        var suite = new TestSuite(
            "IsolationSuite",
            new[]
            {
                new TestCase("cA", "Write", "", null,
                    new[] { TestCaseStep.Create(new DelayStep(1)) },
                    null, Array.Empty<string>()),
                new TestCase("cB", "Assert", "", null,
                    new[] { TestCaseStep.Create(new AssertDidValueStep("k", new byte[] { 0xAA }, 200)) },
                    null, Array.Empty<string>()),
            },
            Array.Empty<string>(), Array.Empty<string>(), new TestSuiteConfig());

        var result = await engine.ExecuteAsync(suite, new StubAssertionContext(), new TestSuiteConfig());

        // case A 写入 Variables["k"]；case B 入口应已清空 → AssertDidValue 超时 Failed（而非读到残留通过）
        Assert.Equal(StepStatus.Passed, result.CaseResults[0].StepResults[0].Status);
        Assert.Equal(StepStatus.Failed, result.CaseResults[1].StepResults[0].Status);
        Assert.Contains("not available", result.CaseResults[1].StepResults[0].Message);
    }
}
