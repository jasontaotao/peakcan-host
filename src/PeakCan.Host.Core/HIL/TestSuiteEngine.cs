using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Core.HIL.Setup;
using PeakCan.Host.Core.HIL.StepExecutor;

namespace PeakCan.Host.Core.HIL;

/// <summary>
/// Test suite execution engine. Orchestrates TestCase execution lifecycle:
/// Suite Setup -> [Case Setup -> Steps -> Case Teardown] x N -> Suite Teardown.
///
/// Sprint 1: orchestration skeleton. End-to-end execution requires Sprint 2 infrastructure.
/// </summary>
public sealed class TestSuiteEngine
{
    private readonly IFixtureResolver _fixtureResolver;
    private readonly IReadOnlyDictionary<TestCaseStepKind, IStepExecutor> _executors;

    public TestSuiteEngine(IFixtureResolver fixtureResolver, IEnumerable<IStepExecutor> executors)
    {
        _fixtureResolver = fixtureResolver;
        _executors = executors.ToDictionary(e => e.Kind);
    }

    public async Task<TestSuiteResult> ExecuteAsync(
        TestSuite suite,
        Contracts.IAssertionContext ctx,
        TestSuiteConfig config,
        IProgress<TestProgress>? progress = null,
        CancellationToken externalCt = default)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        if (suite.TimeoutMs > 0) linkedCts.CancelAfter(suite.TimeoutMs);
        var linkedCt = linkedCts.Token;

        var caseResults = new List<TestCaseResult>();
        var suiteStopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Empty suite: return early
        if (suite.Cases.Count == 0)
        {
            return new TestSuiteResult(suite.Name, 0, 0, 0, 0, 0,
                Array.Empty<string>(), Array.Empty<TestCaseResult>());
        }

        suiteStopwatch.Stop(); // Will be restarted properly below
        suiteStopwatch.Restart();

        // Suite Fixtures
        var suiteFixtures = ResolveFixtures(suite.SuiteFixtureKeys);
        var setupFailures = new List<string>();
        foreach (var fixture in suiteFixtures)
        {
            try { await fixture.SetupAsync(ctx, linkedCt); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                setupFailures.Add($"Setup failed: {ex.Message}");
            }
        }

        bool suiteSetupFailed = setupFailures.Count > 0 && !config.ContinueAfterSetupFailure;

        // Cases
        if (!suiteSetupFailed)
        {
            int caseIndex = 0;
            foreach (var caseModel in suite.Cases)
            {
                linkedCt.ThrowIfCancellationRequested();
                var caseResult = await ExecuteCaseAsync(caseModel, ctx, config, linkedCt);
                caseResults.Add(caseResult);

                progress?.Report(new TestProgress(caseIndex + 1, suite.Cases.Count, caseModel.Name));

                if (!caseResult.Passed && config.FailurePolicy == FailurePolicy.StopSuiteOnFailure)
                    break;

                caseIndex++;
            }
        }

        // Suite Teardown (always, reverse order)
        foreach (var fixture in suiteFixtures.Reverse())
        {
            try { await fixture.TeardownAsync(ctx, linkedCt); }
            catch (Exception ex) { /* log, don't mask */ }
        }

        suiteStopwatch.Stop();

        return new TestSuiteResult(
            SuiteName: suite.Name,
            TotalCases: suite.Cases.Count,
            PassedCases: caseResults.Count(r => r.Passed),
            FailedCases: caseResults.Count(r => !r.Passed),
            SkippedCases: suite.Cases.Count - caseResults.Count,
            ElapsedMs: (int)suiteStopwatch.ElapsedMilliseconds,
            SetupFailures: setupFailures.AsReadOnly(),
            CaseResults: caseResults.AsReadOnly());
    }

    private async Task<TestCaseResult> ExecuteCaseAsync(
        TestCase testCase, Contracts.IAssertionContext ctx, TestSuiteConfig config, CancellationToken ct)
    {
        var stepResults = new List<StepResult>();
        var caseStopwatch = System.Diagnostics.Stopwatch.StartNew();
        string? failureReason = null;

        // Merge global + case-specific fixtures
        var globalFixtures = ResolveFixtures(testCase.CaseFixtureKeys ?? Array.Empty<string>());
        var allFixtures = globalFixtures.ToList();

        // Case Setup
        foreach (var fixture in allFixtures)
        {
            try { await fixture.SetupAsync(ctx, ct); }
            catch (Exception ex)
            {
                failureReason = $"Setup failed: {ex.Message}";
                break;
            }
        }

        // Steps (only if setup succeeded)
        if (failureReason is null)
        {
            for (int i = 0; i < testCase.Steps.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var step = testCase.Steps[i];

                if (step.Kind == TestCaseStepKind.Comment)
                {
                    stepResults.Add(new StepResult(i, step.Kind, step.Label, StepStatus.Comment,
                        $"Comment: {((CommentStep)step.Parameters).Text}", null, null, 0));
                    continue;
                }

                if (!_executors.TryGetValue(step.Kind, out var executor))
                {
                    stepResults.Add(new StepResult(i, step.Kind, step.Label, StepStatus.Failed,
                        $"No executor for kind {step.Kind}", null, null, 0));
                }
                else
                {
                    var stepSw = System.Diagnostics.Stopwatch.StartNew();
                    StepResult result;
                    try
                    {
                        result = await executor.ExecuteAsync(step, ctx, ct);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        result = new StepResult(i, step.Kind, step.Label, StepStatus.Failed,
                            $"Executor threw: {ex.Message}", null, null, 0);
                    }
                    stepSw.Stop();
                    stepResults.Add(result with { StepIndex = i, ElapsedMs = (int)stepSw.ElapsedMilliseconds });
                }

                // Capture FramesAroundFailure on step failure
                if (!stepResults[^1].Passed && stepResults[^1].FramesAroundFailure is null && ctx is IHasRecentFrames hasRecent)
                {
                    stepResults[^1] = stepResults[^1] with
                    {
                        FramesAroundFailure = hasRecent.GetRecentFrames().ToList()
                    };
                }

                // FailurePolicy: StopCaseOnFailure
                if (!stepResults[^1].Passed && config.FailurePolicy == FailurePolicy.StopCaseOnFailure)
                {
                    failureReason = $"Step {i} failed: {stepResults[^1].Message}";
                    // Skip remaining steps
                    for (int j = i + 1; j < testCase.Steps.Count; j++)
                    {
                        stepResults.Add(new StepResult(j, testCase.Steps[j].Kind,
                            testCase.Steps[j].Label, StepStatus.Skipped,
                            "Skipped due to previous failure", null, null, 0));
                    }
                    break;
                }
            }
        }

        // Case Teardown (always, reverse order)
        for (int i = allFixtures.Count - 1; i >= 0; i--)
        {
            var fixture = allFixtures[i];
            try { await fixture.TeardownAsync(ctx, ct); }
            catch (Exception ex)
            {
                failureReason = (failureReason ?? "") + $"; Teardown failed: {ex.Message}";
            }
        }

        caseStopwatch.Stop();

        // Aggregate
        int passedSteps = stepResults.Count(r => r.Status == StepStatus.Passed);
        int failedSteps = stepResults.Count(r => r.Status == StepStatus.Failed);
        int skippedSteps = stepResults.Count(r => r.Status == StepStatus.Skipped);
        int commentSteps = stepResults.Count(r => r.Status == StepStatus.Comment);
        int totalExecutable = passedSteps + failedSteps + skippedSteps;

        bool passed = failureReason is null && stepResults.All(r => r.Status != StepStatus.Failed);

        return new TestCaseResult(
            TestCaseId: testCase.Id,
            TestCaseName: testCase.Name,
            Passed: passed,
            FailureReason: passed ? null : failureReason ?? $"Steps failed: {failedSteps}",
            ElapsedMs: (int)caseStopwatch.ElapsedMilliseconds,
            TotalSteps: totalExecutable,
            PassedSteps: passedSteps,
            FailedSteps: failedSteps,
            SkippedSteps: skippedSteps,
            CommentSteps: commentSteps,
            StepResults: stepResults.AsReadOnly());
    }

    private IReadOnlyList<ITestFixture> ResolveFixtures(IEnumerable<string> keys)
        => keys.Select(key => _fixtureResolver.Resolve(key)).ToList();
}
