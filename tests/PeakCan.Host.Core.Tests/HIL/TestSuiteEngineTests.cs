using PeakCan.Host.Core.HIL;
using PeakCan.Host.Core.HIL.StepExecutor;
using PeakCan.Host.Core.Tests.HIL.Fakes;
using Xunit;

namespace PeakCan.Host.Core.Tests.HIL;

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
}
