using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.StepExecutor;
using PeakCan.HIL.Core.HIL.Setup;
using PeakCan.HIL.Core.Tests.HIL.Fakes;
using Xunit;

namespace PeakCan.HIL.Core.Tests.HIL;

/// <summary>
/// 解释器边界分支补测（对照 TestSuiteEngineInterpreterTests 已覆盖的常规路径）：
/// 只覆盖 ExecuteStepListAsync 中此前没有测试锚定的错误分支——
///   1. Repeat While 守卫恒真跑到 MaxIterations 顶 → "did not converge"
///   2. Repeat MaxIterations 非法值（非数字 / ≤0）→ 容器自身失败，body 不跑
///   3. Repeat Count 引用 undefined 变量 → count 解析失败
///   4. Loop step ≤ 0 / From 非数值 / 空 range
/// 断言口径：容器 Status、body 执行次数、消息关键词——
/// 对消息措辞依赖中间行为的分支用 Match 正则双兼容，避免脆弱断言。
/// </summary>
public class TestSuiteEngineInterpreterBoundaryTests
{
    private static TestSuiteEngine CreateEngine(params IStepExecutor[] executors)
    {
        var fixtureResolver = new FakeFixtureResolver();
        return new TestSuiteEngine(fixtureResolver, executors);
    }

    private static TestCase CreateCase(params TestCaseStep[] steps) => new(
        Id: "case_1", Name: "Boundary Case", Description: "",
        PreConditions: null, Steps: steps, PostConditions: null,
        Tags: Array.Empty<string>(), TimeoutMs: 0, CaseFixtureKeys: null);

    private static TestSuite MakeSuite(TestCase testCase) => new(
        "BoundarySuite", new[] { testCase },
        Array.Empty<string>(), Array.Empty<string>(), new TestSuiteConfig(), 0);

    private static FakeStepExecutor MakeBodyExecutor() => new(TestCaseStepKind.AssertSignal)
    {
        Result = new StepResult(0, TestCaseStepKind.AssertSignal, null, StepStatus.Passed, "body ran", null, null, 0),
    };

    // ── 1. Repeat While 未收敛 ──
    // 引擎行为（TestSuiteEngine Repeat/While 分支）：守卫恒真迭代到 MaxIterations，
    // 退出后 repeatError = "Repeat while did not converge within MaxIterations {n}"。
    // 对照已有 Repeat_MaxIterations_Guard_FailsWhenExceeded（Fixed 超限）与
    // Repeat_While_StopsWhenConditionFalse（guard 假正常退出）——两者都没测本分支。

    [Fact]
    public async Task Repeat_While_NonConvergingGuard_FailsWithConvergeMessage()
    {
        var exec = MakeBodyExecutor();
        var engine = CreateEngine(exec);
        var bodyStep = TestCaseStep.Create(new AssertSignalStep("RPM", "3000.0", "10.0"));
        // guard "1 == 1" 恒真 → 必然撞 MaxIterations=3 上限退出
        var repeatStep = TestCaseStep.Create(new RepeatStep(
            RepeatMode.While, Count: null, Condition: "1 == 1",
            Body: new[] { bodyStep }, MaxIterations: "3"));
        var suite = MakeSuite(CreateCase(repeatStep));

        var result = await engine.ExecuteAsync(suite, new FakeAssertionContext(),
            new TestSuiteConfig(), null, default);

        result.CaseResults[0].Passed.Should().BeFalse("守卫恒真的 while 未收敛属于容器自身失败");
        var container = result.CaseResults[0].StepResults[0];
        container.Status.Should().Be(StepStatus.Failed);
        container.Message.Should().Contain("converge");
        container.Message.Should().Contain("MaxIterations 3");
        exec.ExecuteCallCount.Should().Be(3, "每轮迭代 body 都实际跑了（3 轮后达上限）");
    }

    // ── 2. Repeat MaxIterations 非法值 ──
    // 引擎先 int.TryParse(MaxIterations)，失败即容器失败且从不进入 body。

    [Theory]
    [InlineData("abc", "invalid")]
    [InlineData("0", "must be > 0")]
    [InlineData("-5", "must be > 0")]
    public async Task Repeat_MaxIterationsInvalid_FailsBeforeRunningBody(string maxIterations, string expectedFragment)
    {
        var exec = MakeBodyExecutor();
        var engine = CreateEngine(exec);
        var bodyStep = TestCaseStep.Create(new AssertSignalStep("RPM", "3000.0", "10.0"));
        var repeatStep = TestCaseStep.Create(new RepeatStep(
            RepeatMode.Fixed, Count: "2", Condition: null,
            Body: new[] { bodyStep }, MaxIterations: maxIterations));
        var suite = MakeSuite(CreateCase(repeatStep));

        var result = await engine.ExecuteAsync(suite, new FakeAssertionContext(),
            new TestSuiteConfig(), null, default);

        result.CaseResults[0].Passed.Should().BeFalse();
        var container = result.CaseResults[0].StepResults[0];
        container.Status.Should().Be(StepStatus.Failed);
        container.Message.Should().Contain(expectedFragment);
        exec.ExecuteCallCount.Should().Be(0, "MaxIterations 校验在 body 执行之前");
    }

    // ── 3. Repeat Count 引用 undefined ──
    // ${missing} 在作用域中不存在 → count 求值或整数转换失败；body 从不执行。
    // 错误消息取决于求值器对该表达式的判定（count error 或 not integer），用正则双兼容。

    [Fact]
    public async Task Repeat_CountUndefinedReference_FailsWithoutRunningBody()
    {
        var exec = MakeBodyExecutor();
        var engine = CreateEngine(exec);
        var bodyStep = TestCaseStep.Create(new AssertSignalStep("RPM", "3000.0", "10.0"));
        var repeatStep = TestCaseStep.Create(new RepeatStep(
            RepeatMode.Fixed, Count: "${missing}", Condition: null,
            Body: new[] { bodyStep }, MaxIterations: "10"));
        var suite = MakeSuite(CreateCase(repeatStep));

        var result = await engine.ExecuteAsync(suite, new FakeAssertionContext(),
            new TestSuiteConfig(), null, default);

        result.CaseResults[0].Passed.Should().BeFalse();
        var container = result.CaseResults[0].StepResults[0];
        container.Status.Should().Be(StepStatus.Failed);
        container.Message.Should().Match("*count*");
        exec.ExecuteCallCount.Should().Be(0, "count 无法解析为整数时不进入任何迭代");
    }

    // ── 4. Loop step ≤ 0 ──
    // 引擎明确拒绝非正步长："Loop step must be > 0 (got {v})"。

    [Theory]
    [InlineData("0", "(got 0)")]
    [InlineData("0 - 1", "(got -1)")]
    public async Task Loop_NonPositiveStep_FailsWithExplicitMessage(string stepExpr, string expectedFragment)
    {
        var exec = MakeBodyExecutor();
        var engine = CreateEngine(exec);
        var bodyStep = TestCaseStep.Create(new AssertSignalStep("RPM", "3000.0", "10.0"));
        var loopStep = TestCaseStep.Create(new LoopStep(
            From: "1", To: "5", Step: stepExpr, Body: new[] { bodyStep }, IndexVar: "v"));
        var suite = MakeSuite(CreateCase(loopStep));

        var result = await engine.ExecuteAsync(suite, new FakeAssertionContext(),
            new TestSuiteConfig(), null, default);

        result.CaseResults[0].Passed.Should().BeFalse();
        var container = result.CaseResults[0].StepResults[0];
        container.Status.Should().Be(StepStatus.Failed);
        container.Message.Should().Contain("must be > 0");
        container.Message.Should().Contain(expectedFragment);
        exec.ExecuteCallCount.Should().Be(0);
    }

    // ── 5. Loop From 引用 undefined ──
    // 数值转换失败路径（from/to/step not numeric）；消息来源可能是求值器或类型转换，正则双兼容。

    [Fact]
    public async Task Loop_FromUndefinedReference_FailsWithoutRunningBody()
    {
        var exec = MakeBodyExecutor();
        var engine = CreateEngine(exec);
        var bodyStep = TestCaseStep.Create(new AssertSignalStep("RPM", "3000.0", "10.0"));
        var loopStep = TestCaseStep.Create(new LoopStep(
            From: "${missing}", To: "5", Step: "1", Body: new[] { bodyStep }, IndexVar: "v"));
        var suite = MakeSuite(CreateCase(loopStep));

        var result = await engine.ExecuteAsync(suite, new FakeAssertionContext(),
            new TestSuiteConfig(), null, default);

        result.CaseResults[0].Passed.Should().BeFalse();
        var container = result.CaseResults[0].StepResults[0];
        container.Status.Should().Be(StepStatus.Failed);
        container.Message.Should().Match("*from*");
        exec.ExecuteCallCount.Should().Be(0);
    }

    // ── 6. Loop 空 range（from > to）──
    // 文档化现行为：from=5 to=1 → 循环条件 v <= to 为假 → 零次执行、容器通过、case 通过。
    // 该行为是刻意的静默语义还是应当告警由后续 ADR 决定；此测试先把现状钉住。

    [Fact]
    public async Task Loop_EmptyRange_ZeroIterations_ContainerPasses()
    {
        var exec = MakeBodyExecutor();
        var engine = CreateEngine(exec);
        var bodyStep = TestCaseStep.Create(new AssertSignalStep("RPM", "3000.0", "10.0"));
        var loopStep = TestCaseStep.Create(new LoopStep(
            From: "5", To: "1", Step: "1", Body: new[] { bodyStep }, IndexVar: "v"));
        var suite = MakeSuite(CreateCase(loopStep));

        var result = await engine.ExecuteAsync(suite, new FakeAssertionContext(),
            new TestSuiteConfig(), null, default);

        result.CaseResults[0].Passed.Should().BeTrue("空 range 不产生错误，静默跳过");
        var container = result.CaseResults[0].StepResults[0];
        container.Status.Should().Be(StepStatus.Passed);
        exec.ExecuteCallCount.Should().Be(0);
    }
}
