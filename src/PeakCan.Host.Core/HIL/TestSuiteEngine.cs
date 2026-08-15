using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.HIL.Setup;
using PeakCan.HIL.Core.HIL.StepExecutor;

namespace PeakCan.HIL.Core.HIL;

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
        CancellationToken externalCt = default,
        Contracts.IHilFrameSinkFactory? sinkFactory = null)
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
                var caseResult = await ExecuteCaseAsync(caseModel, ctx, config, linkedCt, caseIndex, sinkFactory);
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
        TestCase testCase, Contracts.IAssertionContext ctx, TestSuiteConfig config, CancellationToken ct,
        int caseIndex, Contracts.IHilFrameSinkFactory? sinkFactory)
    {
        // 清空步骤间变量，防止上一 case 残留值污染（review M-1）：
        // case A 的 ReadDid 写入 did_0xF190，case B 的 AssertDidValue 若读到残留会产生假阳性
        (ctx as IStepVariableStore)?.Variables.Clear();

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

        // Case log sink: setup 成功之后挂载（P6），steps 之前
        Contracts.IHilFrameSink? sink = null;
        if (failureReason is null && ctx is Contracts.IHasFrameSink hasSink && sinkFactory is not null)
        {
            sink = sinkFactory.Create(testCase.Name, caseIndex);
            hasSink.SetFrameSink(sink);
        }

        try
        {
            // Steps (only if setup succeeded) —— 移入 try；finally 负责 sink 拆除（P3）
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

                    // executed 标记步骤是否真正经由执行器产生结果（review finding）：
                    // 引擎合成的失败（No executor 配置错误 / Executor 抛异常）代表步骤从未执行，
                    // 必须保持 Failed 让 case 失败，暴露真实问题；不能被负测试判定提升为 Passed
                    bool executed = false;
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
                            executed = true;
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

                    // ── 负测试判定（ExpectedVerdict 真值表，两分支都必须实现）──

                    // 分支 A：预期 Fail + 实际 Failed + 步骤真正执行过 → 负测试通过，提升 Status 为 Passed。
                    // 未执行的步骤（No executor / Executor 抛异常）不进入本分支，保持 Failed（executed==false）
                    if (executed
                        && step.ExpectedVerdict == ExpectedVerdict.Fail
                        && stepResults[^1].Status == StepStatus.Failed)
                    {
                        stepResults[^1] = stepResults[^1] with
                        {
                            Status = StepStatus.Passed,
                            WasNegatedTest = true,
                            Message = $"Step {i} failed as expected (negated test): {stepResults[^1].Message}",
                        };
                    }
                    // 分支 B：预期 Fail + 实际 Passed → 负测试未生效（如发错误请求却收到成功响应），
                    // 必须判 Failed —— 否则核心场景"如果没返回 NRC 就是失败"会静默误判为通过
                    else if (step.ExpectedVerdict == ExpectedVerdict.Fail)
                    {
                        stepResults[^1] = stepResults[^1] with
                        {
                            Status = StepStatus.Failed,
                            Message = $"Step {i} expected failure but passed (negated test): {stepResults[^1].Message}",
                        };
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
        }
        finally
        {
            // P3: 排空在途帧 → detach → Dispose，顺序不可颠倒
            if (ctx is Contracts.IHasFrameSink hasSink2 && sink is not null)
            {
                await hasSink2.WaitForFrameDrainAsync(ct);
                hasSink2.SetFrameSink(null);
            }
            sink?.Dispose();
        }

        // Case Teardown (always, reverse order) —— finally 之后保持不动
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
