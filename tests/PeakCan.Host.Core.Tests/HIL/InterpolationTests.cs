using FluentAssertions;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.HIL.Setup;
using PeakCan.HIL.Core.HIL.StepExecutor;
using PeakCan.HIL.Core.Tests.HIL.Fakes;
using Xunit;

namespace PeakCan.HIL.Core.Tests.HIL;

/// <summary>
/// B.5 ${name} 插值端到端（spec §15 验收线）。
/// 验证叶步骤执行前 <c>TryInterpolateStep</c> 把 <c>${param.x}</c> 解析为实际值，
/// executor 收到插值后的 step（DelayStep.Milliseconds="200" 而非 "${param.turnMs}"）。
/// 插值在引擎层 ExecuteStepListAsync 叶执行前发生，与 AssignStep 的表达式求值
/// （ExpressionEvaluator 直接解析）是两条不同路径。
/// </summary>
public class InterpolationTests
{
    private static TestSuiteEngine CreateEngine(params IStepExecutor[] executors)
        => new(new FakeFixtureResolver(), executors);

    private static TestCase CreateCase(params TestCaseStep[] steps) => new(
        Id: "case_1", Name: "T", Description: "",
        PreConditions: null, Steps: steps, PostConditions: null,
        Tags: Array.Empty<string>(), TimeoutMs: 0, CaseFixtureKeys: null);

    private static TestSuite SuiteWithParams(TestCaseStep[] steps, Dictionary<string, ParameterValue>? suiteParams = null)
        => new("S", new[] { CreateCase(steps) },
            Array.Empty<string>(), Array.Empty<string>(), new TestSuiteConfig(), 0,
            Parameters: suiteParams);

    [Fact]
    public async Task DelayStep_SuiteParam_Interpolates_ToNumericValue()
    {
        // DelayStep.Milliseconds="${param.turnMs}" + suite param turnMs=200
        // → 引擎 TryInterpolateStep 插值 → DelayStepExecutor int.Parse("200") → "Delayed 200ms"
        var engine = CreateEngine(new DelayStepExecutor());
        var delay = TestCaseStep.Create(new DelayStep("${param.turnMs}"));
        var suiteParams = new Dictionary<string, ParameterValue>
        {
            ["turnMs"] = new(ParameterKind.Number, 200.0),
        };
        var suite = SuiteWithParams(new[] { delay }, suiteParams);

        var result = await engine.ExecuteAsync(suite, new TestCtx(),
            new TestSuiteConfig(), null, default);

        var step = result.CaseResults[0].StepResults[0];
        step.Status.Should().Be(StepStatus.Passed);
        step.Message.Should().Be("Delayed 200ms",
            "suite param turnMs 应在叶执行前插值为 200 再 int.Parse");
    }

    [Fact]
    public async Task UnresolvedInterpolation_Fails_WithUndefinedMessage()
    {
        // ${param.missing} 无对应 suite/case param → 解析为 Undefined → Failed（非静默）
        var engine = CreateEngine(new DelayStepExecutor());
        var delay = TestCaseStep.Create(new DelayStep("${param.missing}"));
        var suite = SuiteWithParams(new[] { delay });

        var result = await engine.ExecuteAsync(suite, new TestCtx(),
            new TestSuiteConfig(), null, default);

        var step = result.CaseResults[0].StepResults[0];
        step.Status.Should().Be(StepStatus.Failed);
        step.Message.Should().Contain("undefined",
            "未解析的插值表达式应报 undefined 并 Failed 而非把字面量传给 int.Parse");
    }

    [Fact]
    public async Task NoInterpolation_FastPath_OriginalStep()
    {
        // 无 ${ 的 step 走 fast path（TryInterpolateStep 原样返回），DelayStep("100") 原样执行
        var engine = CreateEngine(new DelayStepExecutor());
        var delay = TestCaseStep.Create(new DelayStep("100"));
        var suite = SuiteWithParams(new[] { delay });

        var result = await engine.ExecuteAsync(suite, new TestCtx(),
            new TestSuiteConfig(), null, default);

        var step = result.CaseResults[0].StepResults[0];
        step.Status.Should().Be(StepStatus.Passed);
        step.Message.Should().Be("Delayed 100ms");
    }

    /// <summary>测试用 context：IStepVariableStore（Variables 空，Delay 插值用 suite param 不需 var）+ IHasRecentFrames（空）。</summary>
    private sealed class TestCtx : IAssertionContext, IStepVariableStore, IHasRecentFrames
    {
        public IDictionary<string, object> Variables { get; } = new Dictionary<string, object>();
        public double CurrentTimestamp => 0;
        public IReadOnlyList<DecodedFrame> GetRecentDecodedFrames() => Array.Empty<DecodedFrame>();
        public IDisposable SubscribeDecodedFrames(Action<DecodedFrame> onFrame) => new NopDisposable();
        public double? GetSignalValue(string signalName, int maxAgeMs = 5000) => null;
        public ValueTask<Result<Unit>> SendFrameAsync(CanFrame frame, CancellationToken ct)
            => ValueTask.FromResult(Result<Unit>.Ok(default));
        public IReadOnlyList<CanFrame> GetRecentFrames() => Array.Empty<CanFrame>();
        private sealed class NopDisposable : IDisposable { public void Dispose() { } }
    }
}
