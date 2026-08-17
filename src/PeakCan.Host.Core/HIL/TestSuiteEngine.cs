using System.Diagnostics;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.HIL.Expressions;
using PeakCan.HIL.Core.HIL.Setup;
using PeakCan.HIL.Core.HIL.StepExecutor;

namespace PeakCan.HIL.Core.HIL;

/// <summary>
/// Test suite execution engine. Orchestrates TestCase execution lifecycle:
/// Suite Setup -> [Case Setup -> Steps -> Case Teardown] x N -> Suite Teardown.
///
/// v11 H1（§8.2）：删快路径，单解释器路径。所有 suite（含无控制流）统一走
/// <see cref="ExecuteStepListAsync"/> 递归解释器；非控制流 suite 递归退化为扁平循环。
/// <see cref="ExecuteLeafAsync"/> 是唯一叶执行路径（行为逐字保留原 for 循环体）。
/// </summary>
public sealed class TestSuiteEngine
{
    private readonly IFixtureResolver _fixtureResolver;
    private readonly IReadOnlyDictionary<TestCaseStepKind, IStepExecutor> _executors;
    private readonly ExpressionEvaluator _evaluator = new();

    /// <summary>Loop 步骤硬上限（LoopStep 无 MaxIterations 字段，用常量兜底防死循环，§8.3）。</summary>
    private const int MaxLoopIterations = 100_000;

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
        Contracts.IHilFrameSinkFactory? sinkFactory = null,
        // B2-R1：帧统计注入（可空）。null 时 frameCount/frameSeen/elapsedMs 在求值器侧
        // 退化为 UNKNOWN_FUNCTION（Cli 场景可接受）；HilRunnerService 注入真实 collector。
        IFrameStatistics? frameStats = null)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        if (suite.TimeoutMs > 0) linkedCts.CancelAfter(suite.TimeoutMs);
        var linkedCt = linkedCts.Token;

        var caseResults = new List<TestCaseResult>();
        var suiteStopwatch = Stopwatch.StartNew();

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
                var caseResult = await ExecuteCaseAsync(caseModel, ctx, config, linkedCt, caseIndex, sinkFactory, frameStats);
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
            catch (Exception) { /* log, don't mask */ }
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
        int caseIndex, Contracts.IHilFrameSinkFactory? sinkFactory, IFrameStatistics? frameStats)
    {
        // 清空步骤间变量，防止上一 case 拋留值污染（review M-1）：
        // case A 的 ReadDid 写入 did_0xF190，case B 的 AssertDidValue 若读到残留会产生假阳性
        (ctx as IStepVariableStore)?.Variables.Clear();

        var stepResults = new List<StepResult>();
        var caseStopwatch = Stopwatch.StartNew();
        // async 方法不能用 ref，用可变 holder 传播 StopCase 失败原因到 case 级聚合
        var failure = new FailureCtx();

        // Merge global + case-specific fixtures
        var globalFixtures = ResolveFixtures(testCase.CaseFixtureKeys ?? Array.Empty<string>());
        var allFixtures = globalFixtures.ToList();

        // Case Setup
        foreach (var fixture in allFixtures)
        {
            try { await fixture.SetupAsync(ctx, ct); }
            catch (Exception ex)
            {
                failure.Reason = $"Setup failed: {ex.Message}";
                break;
            }
        }

        // Case log sink: setup 成功之后挂载（P6），steps 之前
        Contracts.IHilFrameSink? sink = null;
        if (failure.Reason is null && ctx is Contracts.IHasFrameSink hasSink && sinkFactory is not null)
        {
            sink = sinkFactory.Create(testCase.Name, caseIndex);
            hasSink.SetFrameSink(sink);
        }

        // B2-R1：caseStart = frameStats?.Now ?? 0。frameStats=null 时 caseStart 无意义（FunctionRegistry=null）
        long caseStart = frameStats?.Now ?? 0;

        try
        {
            // Steps (only if setup succeeded) —— 移入 try；finally 负责 sink 拆除（P3）
            if (failure.Reason is null)
            {
                // 构造 StepScope（v11.1 Ruling 1：host 注入 core StepScope）。
                // ctx 不实现 IStepVariableStore 时 store=null → Variables 层为 null（${name} 退化为 Undefined，
                // 非控制流 suite 不用表达式，无影响）。
                var scope = StepScopeFactory.Create(
                    ctx, ctx as IStepVariableStore, frameStats, caseStart,
                    suiteParams: null, caseParams: testCase.Parameters);

                // v11 H1：单解释器路径。非控制流 suite 递归退化为扁平循环（顶层步骤列表，无嵌套 body）。
                await ExecuteStepListAsync(
                    testCase.Steps, scope, ctx, ct,
                    containerStepIndex: null, pathPrefix: null,
                    config, stepResults, iteration: null,
                    frameStats, caseStart, failure);
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
                failure.Reason = (failure.Reason ?? "") + $"; Teardown failed: {ex.Message}";
            }
        }

        caseStopwatch.Stop();

        // Aggregate
        int passedSteps = stepResults.Count(r => r.Status == StepStatus.Passed);
        int failedSteps = stepResults.Count(r => r.Status == StepStatus.Failed);
        int skippedSteps = stepResults.Count(r => r.Status == StepStatus.Skipped);
        int commentSteps = stepResults.Count(r => r.Status == StepStatus.Comment);
        int totalExecutable = passedSteps + failedSteps + skippedSteps;

        bool passed = failure.Reason is null && stepResults.All(r => r.Status != StepStatus.Failed);

        return new TestCaseResult(
            TestCaseId: testCase.Id,
            TestCaseName: testCase.Name,
            Passed: passed,
            FailureReason: passed ? null : failure.Reason ?? $"Steps failed: {failedSteps}",
            ElapsedMs: (int)caseStopwatch.ElapsedMilliseconds,
            TotalSteps: totalExecutable,
            PassedSteps: passedSteps,
            FailedSteps: failedSteps,
            SkippedSteps: skippedSteps,
            CommentSteps: commentSteps,
            StepResults: stepResults.AsReadOnly());
    }

    // ─────────────────────────────────────────────────────────────────────
    //  v11 H1 单解释器路径：ExecuteStepListAsync（递归）+ ExecuteLeafAsync
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 递归解释器：遍历步骤列表，按 kind 分派。
    /// - 叶步骤（非控制流）→ <see cref="ExecuteLeafAsync"/>
    /// - Comment → 原样 StepResult
    /// - If/Repeat/Loop/Assign → 解释执行（body 递归）
    /// - StopCaseOnFailure：叶失败 → 跳过当前列表剩余兄弟（补 Skipped）+ break（§6.5 M2：
    ///   只中断当前列表，外层继续）
    /// </summary>
    /// <param name="containerStepIndex">当前列表所属容器的顶层 StepIndex（null=顶层列表，每步用自身 index）。</param>
    /// <param name="pathPrefix">父路径前缀（null=顶层，顶层叶 Path=null 向后兼容旧 JSON）。</param>
    /// <param name="iteration">外层循环当前次（null=非循环上下文）。</param>
    /// <param name="failure">可变失败追踪器（async 方法不能用 ref，用引用类型 holder 传播 StopCase 失败原因到 case 级聚合）。</param>
    private async Task ExecuteStepListAsync(
        IReadOnlyList<TestCaseStep> steps,
        StepScope scope,
        Contracts.IAssertionContext ctx,
        CancellationToken ct,
        int? containerStepIndex,
        string? pathPrefix,
        TestSuiteConfig config,
        List<StepResult> stepResults,
        int? iteration,
        IFrameStatistics? frameStats,
        long caseStart,
        FailureCtx failure)
    {
        for (int i = 0; i < steps.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var step = steps[i];

            // ⑤ StepIndex/Path 填充规则：
            // - StepIndex = 外层透传的顶层 index（递归进 body 时不递增，body 内所有叶共享外层容器的 StepIndex）
            // - Path = 父 Path + "." + body 内序号（顶层叶 Path=null，向后兼容旧 JSON）
            int stepIndex = containerStepIndex ?? i;
            // pathSegment：当前步骤的路径段。顶层（pathPrefix=null）→ 顶层叶/容器的 recordedPath=null，
            // 但其 body 子步骤的 pathPrefix = i.ToString()（如顶层 index 1 的 If → body 子 Path="1.0"）
            string? recordedPath = pathPrefix is null ? null : $"{pathPrefix}.{i}";
            string? childPathPrefix = pathPrefix is null ? i.ToString() : recordedPath;

            // ── Comment：原样记录（不计入通过/失败）──
            if (step.Kind == TestCaseStepKind.Comment)
            {
                stepResults.Add(new StepResult(stepIndex, step.Kind, step.Label, StepStatus.Comment,
                    $"Comment: {((CommentStep)step.Parameters).Text}", null, null, 0)
                { Path = recordedPath, Iteration = iteration });
                continue;
            }

            // ── Assign：求值 Expression → 写 Variables[Assign]（无 CAN I/O）──
            if (step.Kind == TestCaseStepKind.Assign)
            {
                var assignParams = (AssignStep)step.Parameters;
                var eval = _evaluator.Evaluate(assignParams.Expression, scope);
                StepResult assignResult;
                if (eval.IsSuccess)
                {
                    // 写入 IStepVariableStore.Variables（ctx 不实现时为 no-op）。
                    // Undefined 结果 ToObject 返回 null；Dictionary<string,object> 允许 null 值，
                    // 读回经 ConvertObjectToExpressionValue(null) → Undefined（§5.5 一等值）。
                    var assignValue = ToObject(eval.Value);
                    if (ctx is IStepVariableStore store)
                        store.Variables[assignParams.Assign] = assignValue!;
                    assignResult = new StepResult(stepIndex, step.Kind, step.Label, StepStatus.Passed,
                        $"Assigned {assignParams.Assign} = {eval.Value}",
                        ActualValue: eval.Value.ToString(), null, 0)
                    { Path = recordedPath, Iteration = iteration };
                }
                else
                {
                    assignResult = new StepResult(stepIndex, step.Kind, step.Label, StepStatus.Failed,
                        $"Assign expression error: {eval.Error.Message}", null, null, 0)
                    { Path = recordedPath, Iteration = iteration };
                }
                stepResults.Add(assignResult);
                // Assign 写入后刷新 scope.Variables，使后续 ${name} 引用能读到（§7 读穿透）
                scope = RefreshScope(scope, ctx);
                // Assign 失败 + StopCase → 跳过剩余兄弟
                if (!assignResult.Passed && config.FailurePolicy == FailurePolicy.StopCaseOnFailure)
                {
                    RecordStopCaseSkip(steps, i + 1, stepIndex, pathPrefix, containerStepIndex, iteration, stepResults, ct);
                    if (failure.Reason is null) failure.Reason = $"Step {stepIndex} failed: {assignResult.Message}";
                    break;
                }
                continue;
            }

            // ── If：求值 condition → true 走 Body / false 走 ElseBody（§5.5 undefined 语义）──
            if (step.Kind == TestCaseStepKind.If)
            {
                var ifParams = (IfStep)step.Parameters;
                var (condOk, branchTrue, condWarning, condError) = EvaluateIfCondition(ifParams.Condition, scope);

                StepResult ifContainer;
                int childrenStart = stepResults.Count;

                if (!condOk)
                {
                    // 条件求值错误（非 undefined 单引用）→ 容器 Failed
                    ifContainer = new StepResult(stepIndex, step.Kind, step.Label, StepStatus.Failed,
                        $"if condition error: {condError}", null, null, 0)
                    { Path = recordedPath, Iteration = iteration };
                    stepResults.Add(ifContainer);
                }
                else
                {
                    // 选择分支并递归执行 body
                    var body = branchTrue ? ifParams.Body : ifParams.ElseBody;
                    if (body is { Count: > 0 })
                    {
                        await ExecuteStepListAsync(body, scope, ctx, ct,
                            containerStepIndex: stepIndex, pathPrefix: childPathPrefix,
                            config, stepResults, iteration, frameStats, caseStart, failure);
                        // body 内 Assign/ReadDid 可能写入 Variables → 刷新 scope，使后续兄弟步骤可读
                        scope = RefreshScope(scope, ctx);
                    }
                    ifContainer = BuildContainerResult(stepIndex, step.Kind, step.Label,
                        stepResults, childrenStart, "if", recordedPath, iteration,
                        warning: condWarning);
                    stepResults.Insert(childrenStart, ifContainer);
                }

                // 容器失败 + StopCase → 跳过剩余兄弟
                if (!ifContainer.Passed && config.FailurePolicy == FailurePolicy.StopCaseOnFailure)
                {
                    RecordStopCaseSkip(steps, i + 1, stepIndex, pathPrefix, containerStepIndex, iteration, stepResults, ct);
                    if (failure.Reason is null) failure.Reason =$"Step {stepIndex} failed: {ifContainer.Message}";
                    break;
                }
                continue;
            }

            // ── Repeat：Fixed（Count 次）/ While（guard，每迭代 ct 检查 + MaxIterations 守卫）──
            if (step.Kind == TestCaseStepKind.Repeat)
            {
                var rp = (RepeatStep)step.Parameters;
                int childrenStart = stepResults.Count;
                string? repeatError = null;

                if (rp.Mode == RepeatMode.Fixed)
                {
                    int count = 1;
                    var cEval = _evaluator.Evaluate(rp.Count ?? "1", scope);
                    if (!cEval.IsSuccess) repeatError = $"Repeat count error: {cEval.Error.Message}";
                    else if (!TryToInt(cEval.Value, out count)) repeatError = $"Repeat count not integer: {cEval.Value}";

                    if (repeatError is null)
                    {
                        if (rp.MaxIterations <= 0)
                            repeatError = $"Repeat MaxIterations must be > 0 (got {rp.MaxIterations})";
                        else if (count > rp.MaxIterations)
                            repeatError = $"Repeat exceeded MaxIterations {rp.MaxIterations} (requested {count})";
                        else
                        {
                            for (int k = 0; k < count; k++)
                            {
                                ct.ThrowIfCancellationRequested();
                                var iterScope = WithIndexVar(scope, rp.IndexVar, ExpressionValue.FromLong(k));
                                await ExecuteStepListAsync(rp.Body, iterScope, ctx, ct,
                                    containerStepIndex: stepIndex, pathPrefix: childPathPrefix,
                                    config, stepResults, iteration: k, frameStats, caseStart, failure);
                                // body 内 Assign/ReadDid 写入 Variables → 刷新 scope，使下一迭代可读
                                scope = RefreshScope(scope, ctx);
                            }
                        }
                    }
                }
                else // While
                {
                    int k = 0;
                    while (k < rp.MaxIterations)
                    {
                        ct.ThrowIfCancellationRequested();
                        var iterScope = WithIndexVar(scope, rp.IndexVar, ExpressionValue.FromLong(k));
                        var guard = EvaluateWhileGuard(rp.Condition ?? "false", iterScope);
                        if (!guard.Success)
                        {
                            repeatError = guard.Error;  // §5.5：while 守卫 undefined → Failed
                            break;
                        }
                        if (!guard.Value) break;  // 条件 false → 退出循环
                        await ExecuteStepListAsync(rp.Body, iterScope, ctx, ct,
                            containerStepIndex: stepIndex, pathPrefix: childPathPrefix,
                            config, stepResults, iteration: k, frameStats, caseStart, failure);
                        // body 内 Assign/ReadDid 写入 Variables → 刷新 scope，使下一迭代 guard 可读
                        scope = RefreshScope(scope, ctx);
                        k++;
                    }
                    if (repeatError is null && k >= rp.MaxIterations && rp.MaxIterations > 0)
                        repeatError = $"Repeat while did not converge within MaxIterations {rp.MaxIterations}";
                }

                var repeatContainer = BuildContainerResult(stepIndex, step.Kind, step.Label,
                    stepResults, childrenStart, "repeat", recordedPath, iteration,
                    error: repeatError);
                stepResults.Insert(childrenStart, repeatContainer);

                if (!repeatContainer.Passed && config.FailurePolicy == FailurePolicy.StopCaseOnFailure)
                {
                    RecordStopCaseSkip(steps, i + 1, stepIndex, pathPrefix, containerStepIndex, iteration, stepResults, ct);
                    if (failure.Reason is null) failure.Reason =$"Step {stepIndex} failed: {repeatContainer.Message}";
                    break;
                }
                continue;
            }

            // ── Loop：From→To（含）按 Step 递增，IndexVar 绑定当前值 ──
            if (step.Kind == TestCaseStepKind.Loop)
            {
                var lp = (LoopStep)step.Parameters;
                int childrenStart = stepResults.Count;
                string? loopError = null;

                var fEval = _evaluator.Evaluate(lp.From, scope);
                var tEval = _evaluator.Evaluate(lp.To, scope);
                var sEval = _evaluator.Evaluate(lp.Step, scope);
                if (!fEval.IsSuccess) loopError = $"Loop from error: {fEval.Error.Message}";
                else if (!tEval.IsSuccess) loopError = $"Loop to error: {tEval.Error.Message}";
                else if (!sEval.IsSuccess) loopError = $"Loop step error: {sEval.Error.Message}";

                // 先声明（|| 短路时编译器无法证明已赋值，初始化 0 兜底）
                double fromVal = 0, toVal = 0, stepVal = 0;
                if (loopError is null
                    && (!TryToDouble(fEval.Value, out fromVal)
                        || !TryToDouble(tEval.Value, out toVal)
                        || !TryToDouble(sEval.Value, out stepVal)))
                {
                    loopError = "Loop from/to/step not numeric";
                }

                if (loopError is null)
                {
                    if (stepVal <= 0)
                        loopError = $"Loop step must be > 0 (got {stepVal})";
                    else
                    {
                        int k = 0;
                        for (double v = fromVal; v <= toVal + 1e-9 && loopError is null; v += stepVal)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (k >= MaxLoopIterations)
                            {
                                loopError = $"Loop exceeded hard limit {MaxLoopIterations}";
                                break;
                            }
                            var iterScope = WithIndexVar(scope, lp.IndexVar, ExpressionValue.FromDouble(v));
                            await ExecuteStepListAsync(lp.Body, iterScope, ctx, ct,
                                containerStepIndex: stepIndex, pathPrefix: childPathPrefix,
                                config, stepResults, iteration: k, frameStats, caseStart, failure);
                            // body 内 Assign/ReadDid 写入 Variables → 刷新 scope，使下一迭代可读
                            scope = RefreshScope(scope, ctx);
                            k++;
                        }
                    }
                }

                var loopContainer = BuildContainerResult(stepIndex, step.Kind, step.Label,
                    stepResults, childrenStart, "loop", recordedPath, iteration,
                    error: loopError);
                stepResults.Insert(childrenStart, loopContainer);

                if (!loopContainer.Passed && config.FailurePolicy == FailurePolicy.StopCaseOnFailure)
                {
                    RecordStopCaseSkip(steps, i + 1, stepIndex, pathPrefix, containerStepIndex, iteration, stepResults, ct);
                    if (failure.Reason is null) failure.Reason =$"Step {stepIndex} failed: {loopContainer.Message}";
                    break;
                }
                continue;
            }

            // ── 叶步骤（executor 分派）── 逐字保留原 for 循环体（executed/负测试两分支/帧捕获）
            var result = await ExecuteLeafAsync(step, ctx, ct, stepIndex, recordedPath, iteration);
            stepResults.Add(result);
            // 叶步骤可能写入 Variables（如 ReadDid）→ 刷新 scope，使后续 ${name} 可读
            scope = RefreshScope(scope, ctx);

            // FailurePolicy: StopCaseOnFailure —— 列表级：叶失败 → 跳过当前列表剩余兄弟 + break
            // （§6.5 M2：只中断当前列表，外层继续；负测试提升后的 Passed 不触发）
            if (!result.Passed && config.FailurePolicy == FailurePolicy.StopCaseOnFailure)
            {
                if (failure.Reason is null) failure.Reason = $"Step {stepIndex} failed: {result.Message}";
                RecordStopCaseSkip(steps, i + 1, stepIndex, pathPrefix, containerStepIndex, iteration, stepResults, ct);
                break;
            }
        }
    }

    /// <summary>
    /// 执行单个叶步骤（行为逐字保留原 for 循环体）。
    /// 负责：executor 解析 / ExecuteAsync / executed 标记 / 负测试两分支 A/B / FramesAroundFailure 捕获。
    /// 不负责：StopCase 跳过后续兄弟（列表级逻辑，在 <see cref="ExecuteStepListAsync"/>）。
    /// 返回自身的最终 StepResult（负测试 in-place 改自己经返回值带出，不改兄弟，§⑦）。
    /// </summary>
    private async Task<StepResult> ExecuteLeafAsync(
        TestCaseStep step, Contracts.IAssertionContext ctx, CancellationToken ct,
        int stepIndex, string? path, int? iteration)
    {
        // executed 标记步骤是否真正经由执行器产生结果（review finding）：
        // 引擎合成的失败（No executor 配置错误 / Executor 抛异常）代表步骤从未执行，
        // 必须保持 Failed 让 case 失败，暴露真实问题；不能被负测试判定提升为 Passed
        bool executed = false;
        StepResult result;
        if (!_executors.TryGetValue(step.Kind, out var executor))
        {
            result = new StepResult(stepIndex, step.Kind, step.Label, StepStatus.Failed,
                $"No executor for kind {step.Kind}", null, null, 0)
            { Path = path, Iteration = iteration };
        }
        else
        {
            var stepSw = Stopwatch.StartNew();
            try
            {
                result = await executor.ExecuteAsync(step, ctx, ct);
                executed = true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                result = new StepResult(stepIndex, step.Kind, step.Label, StepStatus.Failed,
                    $"Executor threw: {ex.Message}", null, null, 0)
                { Path = path, Iteration = iteration };
            }
            stepSw.Stop();
            result = result with { StepIndex = stepIndex, ElapsedMs = (int)stepSw.ElapsedMilliseconds, Path = path, Iteration = iteration };
        }

        // ── 负测试判定（ExpectedVerdict 真值表，两分支都必须实现）──

        // 分支 A：预期 Fail + 实际 Failed + 步骤真正执行过 → 负测试通过，提升 Status 为 Passed。
        // 未执行的步骤（No executor / Executor 抛异常）不进入本分支，保持 Failed（executed==false）
        if (executed
            && step.ExpectedVerdict == ExpectedVerdict.Fail
            && result.Status == StepStatus.Failed)
        {
            result = result with
            {
                Status = StepStatus.Passed,
                WasNegatedTest = true,
                Message = $"Step {stepIndex} failed as expected (negated test): {result.Message}",
            };
        }
        // 分支 B：预期 Fail + 实际 Passed → 负测试未生效（如发错误请求却收到成功响应），
        // 必须判 Failed —— 否则核心场景"如果没返回 NRC 就是失败"会静默误判为通过
        else if (step.ExpectedVerdict == ExpectedVerdict.Fail)
        {
            result = result with
            {
                Status = StepStatus.Failed,
                Message = $"Step {stepIndex} expected failure but passed (negated test): {result.Message}",
            };
        }

        // Capture FramesAroundFailure on step failure（在两负测试分支之后、依赖 ctx is IHasRecentFrames）
        if (!result.Passed && result.FramesAroundFailure is null && ctx is Contracts.IHasRecentFrames hasRecent)
        {
            result = result with
            {
                FramesAroundFailure = hasRecent.GetRecentFrames().ToList()
            };
        }

        return result;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  辅助：条件求值 / 容器聚合 / scope 刷新 / StopCase 跳过填充
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// if 条件求值（§5.5）：
    /// - 单引用（VariableRef / SourceRef）undefined → falsy（走 else）+ warning（不 Failed）
    /// - compound 表达式遇 undefined → ExpressionError → Failed
    /// - 非 bool 结果 → Failed
    /// </summary>
    private (bool Success, bool Value, string? Warning, string? Error) EvaluateIfCondition(string condition, StepScope scope)
    {
        var parse = _evaluator.Parse(condition);
        if (parse.IsError)
            return (false, false, null, $"parse error: {parse.Error!.Message}");

        bool isSingleRef = parse.Ast is VariableRef or SourceRef;

        var eval = _evaluator.Evaluate(parse.Ast!, scope);
        if (eval.IsSuccess)
        {
            if (eval.Value.Kind == ExpressionValue.ValueKind.Bool)
                return (true, eval.Value.AsBool, null, null);
            if (eval.Value.Kind == ExpressionValue.ValueKind.Undefined && isSingleRef)
                return (true, false, "if condition reference undefined — treated as false (§5.5)", null);
            return (false, false, null, $"condition evaluated to non-boolean {eval.Value.Kind}");
        }
        return (false, false, null, eval.Error.Message);
    }

    /// <summary>
    /// while/repeat 守卫求值（§5.5）：
    /// - undefined（单引用或运算）→ Failed（消息点名缺失引用并开药方）
    /// - 非 bool → Failed
    /// </summary>
    private (bool Success, bool Value, string? Error) EvaluateWhileGuard(string condition, StepScope scope)
    {
        var parse = _evaluator.Parse(condition);
        if (parse.IsError)
            return (false, false, $"guard parse error: {parse.Error!.Message}");

        var eval = _evaluator.Evaluate(parse.Ast!, scope);
        if (eval.IsSuccess)
        {
            if (eval.Value.Kind == ExpressionValue.ValueKind.Bool)
                return (true, eval.Value.AsBool, null);
            if (eval.Value.Kind == ExpressionValue.ValueKind.Undefined)
                return (false, false, "while guard undefined: reference not initialized; add AssignStep(assign=<var>, expression=0) before the loop to initialize (§5.5)");
            return (false, false, $"guard evaluated to non-boolean {eval.Value.Kind}");
        }
        return (false, false, $"guard error: {eval.Error.Message}");
    }

    /// <summary>
    /// 构造容器 StepResult（§6.5：容器不产生独立判定，聚合子步骤状态）。
    /// 子步骤已在 [childrenStart, stepResults.Count) 范围内（容器自身尚未插入）。
    /// </summary>
    private static StepResult BuildContainerResult(
        int stepIndex, TestCaseStepKind kind, string? label,
        List<StepResult> stepResults, int childrenStart, string summaryPrefix,
        string? path, int? iteration, string? warning = null, string? error = null)
    {
        var children = stepResults.Skip(childrenStart).ToList();
        bool anyFailed = children.Any(c => c.Status == StepStatus.Failed);
        int passedCount = children.Count(c => c.Status == StepStatus.Passed);
        var status = (error is not null || anyFailed) ? StepStatus.Failed : StepStatus.Passed;
        var msg = $"{summaryPrefix}: {passedCount}/{children.Count} steps passed";
        if (error is not null) msg += $"; {error}";
        else if (warning is not null) msg += $"; {warning}";
        return new StepResult(stepIndex, kind, label, status, msg, null, null, 0)
        { Path = path, Iteration = iteration };
    }

    /// <summary>
    /// StopCase 跳过：为当前列表 [from, steps.Count) 的兄弟补 Skipped StepResult。
    /// ⑤ StepIndex/Path/Iteration 与正常步骤同规则（共享容器 StepIndex、Path=父前缀.序号、Iteration 继承）。
    /// </summary>
    private static void RecordStopCaseSkip(
        IReadOnlyList<TestCaseStep> steps, int fromIndex,
        int containerStepIndex, string? pathPrefix, int? containerStepIndexParam,
        int? iteration, List<StepResult> stepResults, CancellationToken ct)
    {
        for (int j = fromIndex; j < steps.Count; j++)
        {
            ct.ThrowIfCancellationRequested();
            var sib = steps[j];
            int sibStepIndex = containerStepIndexParam ?? j;
            string? sibPath = pathPrefix is null ? null : $"{pathPrefix}.{j}";
            stepResults.Add(new StepResult(sibStepIndex, sib.Kind, sib.Label, StepStatus.Skipped,
                "Skipped due to previous failure", null, null, 0)
            { Path = sibPath, Iteration = iteration });
        }
    }

    /// <summary>循环 IndexVar 绑定：压栈当前绑定，原 LoopIndexVar 降为 OuterLoopIndexVar（innermost wins，§7）。</summary>
    private static StepScope WithIndexVar(StepScope scope, string? indexVar, ExpressionValue value)
    {
        if (indexVar is null) return scope;
        var binding = new Dictionary<string, ExpressionValue>(1) { [indexVar] = value };
        return scope with { LoopIndexVar = binding, OuterLoopIndexVar = scope.LoopIndexVar };
    }

    /// <summary>ExpressionValue → object（写入 IStepVariableStore.Variables；与 HostDidValueResolver.ConvertObjectToExpressionValue 互逆）。</summary>
    private static object? ToObject(ExpressionValue v) => v.Kind switch
    {
        ExpressionValue.ValueKind.Double => v.AsDouble,
        ExpressionValue.ValueKind.Long => v.AsLong,
        ExpressionValue.ValueKind.Bool => v.AsBool,
        ExpressionValue.ValueKind.String => v.AsString,
        ExpressionValue.ValueKind.Bytes => v.AsBytes,
        _ => null,  // Undefined
    };

    /// <summary>ExpressionValue → double（Loop from/to/step）。</summary>
    private static bool TryToDouble(ExpressionValue v, out double result)
    {
        if (v.Kind == ExpressionValue.ValueKind.Double) { result = v.AsDouble; return true; }
        if (v.Kind == ExpressionValue.ValueKind.Long) { result = v.AsLong; return true; }
        result = 0; return false;
    }

    /// <summary>ExpressionValue → int（Repeat Count）。</summary>
    private static bool TryToInt(ExpressionValue v, out int result)
    {
        if (v.Kind == ExpressionValue.ValueKind.Long && v.AsLong >= 0 && v.AsLong <= int.MaxValue)
        { result = (int)v.AsLong; return true; }
        if (v.Kind == ExpressionValue.ValueKind.Double && v.AsDouble >= 0 && v.AsDouble <= int.MaxValue && v.AsDouble == Math.Truncate(v.AsDouble))
        { result = (int)v.AsDouble; return true; }
        result = 0; return false;
    }

    /// <summary>从 IStepVariableStore 重建 scope.Variables 快照，使 ${name} 读到 Assign/ReadDid 的最新写入（§7 读穿透）。</summary>
    private static StepScope RefreshScope(StepScope scope, Contracts.IAssertionContext ctx)
        => ctx is IStepVariableStore store
            ? scope with { Variables = StepScopeFactory.RefreshVariables(store) }
            : scope;

    /// <summary>
    /// 可变失败原因 holder。async 方法不能用 ref 参数，用引用类型实例
    /// 在递归 ExecuteStepListAsync 间共享 StopCase 失败原因，传播到 case 级聚合。
    /// 只记录首次失败（guard: if Reason is null）。
    /// </summary>
    private sealed class FailureCtx
    {
        public string? Reason;
    }

    private IReadOnlyList<ITestFixture> ResolveFixtures(IEnumerable<string> keys)
        => keys.Select(key => _fixtureResolver.Resolve(key)).ToList();
}
