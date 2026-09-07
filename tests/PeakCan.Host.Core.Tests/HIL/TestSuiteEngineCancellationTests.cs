using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.HIL.StepExecutor;
using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Core.Tests.HIL.Fakes;
using PeakCan.Host.Core.HIL;
using PeakCan.Host.Core.HIL.Setup;
using PeakCan.Host.Core.HIL.StepExecutor;
using Xunit;

namespace PeakCan.Host.Core.Tests.HIL;

public class TestSuiteEngineCancellationTests
{
    [Fact]
    public async Task UserCancel_DuringStep_ReturnsPartial_And_RunsTeardownsWithNone()
    {
        var fixture = new CancelOnExecuteFixture();
        var resolver = new RecordingResolver(fixture);
        var executor = new CancellingExecutor(fixture);
        var engine = new TestSuiteEngine(resolver, new IStepExecutor[] { executor });
        var suite = MakeSuite(fixtureKey: "cancel");

        var result = await engine.ExecuteAsync(suite, new FakeAssertionContext(), new TestSuiteConfig(), null, fixture.Token);

        Assert.Single(result.CaseResults);
        Assert.Equal(1, result.SkippedCases);
        Assert.False(result.CaseResults[0].Passed);
        Assert.Equal("已取消", result.CaseResults[0].FailureReason);

        var teardownToken = Assert.Single(fixture.TeardownTokens); Assert.Equal(CancellationToken.None, teardownToken);
    }

    [Fact]
    public async Task UserCancel_DuringCaseSetup_ReturnsFailedCancelledCase()
    {
        var fixture = new CancelOnExecuteFixture(cancelBeforeExecution: true);
        var resolver = new RecordingResolver(fixture);
        var engine = new TestSuiteEngine(resolver, Array.Empty<IStepExecutor>());
        var suite = MakeSuite(fixtureKey: "cancel");

        var result = await engine.ExecuteAsync(suite, new FakeAssertionContext(), new TestSuiteConfig(), null, fixture.Token);

        Assert.Single(result.CaseResults);
        Assert.Equal(1, result.SkippedCases);
        Assert.False(result.CaseResults[0].Passed);
        Assert.Equal("已取消", result.CaseResults[0].FailureReason);
        var teardownToken = Assert.Single(fixture.TeardownTokens); Assert.Equal(CancellationToken.None, teardownToken);
    }

    [Fact]
    public async Task SuiteTimeout_ReturnsPartialTimeout_NotCancelled()
    {
        var fixture = new CancelOnExecuteFixture();
        var resolver = new RecordingResolver(fixture);
        var executor = new DelayingExecutor();
        var engine = new TestSuiteEngine(resolver, new IStepExecutor[] { executor });
        var suite = MakeSuite(fixtureKey: "cancel", timeoutMs: 10);

        var result = await engine.ExecuteAsync(suite, new FakeAssertionContext(), new TestSuiteConfig(), null, fixture.Token);

        Assert.Single(result.CaseResults);
        Assert.Equal(1, result.SkippedCases);
        Assert.False(result.CaseResults[0].Passed);
        Assert.Equal("套件超时", result.CaseResults[0].FailureReason);
        var teardownToken = Assert.Single(fixture.TeardownTokens); Assert.Equal(CancellationToken.None, teardownToken);
    }

    [Fact]
    public async Task UserCancel_DuringSuiteSetup_ThrowsOperationCanceledException()
    {
        var fixture = new CancelOnExecuteFixture(cancelBeforeExecution: true);
        var resolver = new RecordingResolver(fixture);
        var engine = new TestSuiteEngine(resolver, Array.Empty<IStepExecutor>());
        var suite = MakeSuite(suiteFixtureKey: "cancel");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.ExecuteAsync(suite, new FakeAssertionContext(), new TestSuiteConfig(), null, fixture.Token));
    }

    private static TestSuite MakeSuite(
        string? fixtureKey = null,
        string? suiteFixtureKey = null,
        int timeoutMs = 0)
    {
        var caseFixtures = fixtureKey is null ? null : new[] { fixtureKey };
        var caseStep = TestCaseStep.Create(new AssertSignalStep("RPM", "3000.0", "10.0"));
        var testCase = new TestCase(
            Id: "case_1", Name: "Case 1", Description: "",
            PreConditions: null, Steps: new[] { caseStep }, PostConditions: null,
            Tags: Array.Empty<string>(), TimeoutMs: 0, CaseFixtureKeys: caseFixtures);
        var secondCase = testCase with { Id = "case_2", Name = "Case 2" };
        return new TestSuite(
            "S", new[] { testCase, secondCase },
            Array.Empty<string>(),
            suiteFixtureKey is null ? Array.Empty<string>() : new[] { suiteFixtureKey },
            new TestSuiteConfig(), timeoutMs);
    }
    private sealed class RecordingResolver : IFixtureResolver
    {
        private readonly CancelOnExecuteFixture _fixture;
        public RecordingResolver(CancelOnExecuteFixture fixture) => _fixture = fixture;
        public ITestFixture Resolve(string key) => _fixture;
    }

    private sealed class CancelOnExecuteFixture : ITestFixture, IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        public CancellationToken Token => _cts.Token;
        public List<CancellationToken> SetupTokens { get; } = new();
        public List<CancellationToken> TeardownTokens { get; } = new();

        public CancelOnExecuteFixture(bool cancelBeforeExecution = false)
        {
            if (cancelBeforeExecution) _cts.Cancel();
        }

        public Task SetupAsync(IAssertionContext ctx, CancellationToken ct)
        {
            SetupTokens.Add(ct);
            Token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task TeardownAsync(IAssertionContext ctx, CancellationToken ct)
        {
            TeardownTokens.Add(ct);
            return Task.CompletedTask;
        }

        public void Cancel() => _cts.Cancel();

        public void Dispose() => _cts.Dispose();
    }

    private sealed class CancellingExecutor : IStepExecutor
    {
        private readonly CancelOnExecuteFixture _fixture;
        public CancellingExecutor(CancelOnExecuteFixture fixture) => _fixture = fixture;

        public TestCaseStepKind Kind => TestCaseStepKind.AssertSignal;

        public Task<StepResult> ExecuteAsync(TestCaseStep step, IAssertionContext ctx, CancellationToken ct)
        {
            _fixture.Cancel();
            throw new OperationCanceledException(_fixture.Token);
        }
    }

    private sealed class DelayingExecutor : IStepExecutor
    {
        public TestCaseStepKind Kind => TestCaseStepKind.AssertSignal;

        public async Task<StepResult> ExecuteAsync(TestCaseStep step, IAssertionContext ctx, CancellationToken ct)
        {
            await Task.Delay(1000, ct);
            return new StepResult(0, Kind, step.Label, StepStatus.Passed, "ok", null, null, 0);
        }
    }
}
