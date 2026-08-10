using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.StepExecutor;
using PeakCan.HIL.Core.Tests.HIL.Fakes;
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
}
