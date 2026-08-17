using System.IO;
using System.Reflection;
using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.HIL.Setup;
using PeakCan.HIL.Core.HIL.StepExecutor;
using PeakCan.HIL.Core.Tests.HIL.Fakes;

namespace PeakCan.HIL.Core.Tests.HIL;

/// <summary>
/// B.2 回归基线 + 控制流解释器测试（v11 H1 单路径）。
/// golden 基线在重构前（feature 分支、抽 ExecuteLeafAsync 之前）跑一次存盘，
/// 重构后比对语义等价（Status/Message/FramesAroundFailure + 非控制流 Path=null）。
/// 控制流用例（If/Repeat/Loop/Assign）在重构前为红（引擎返回 No executor for kind If/...），
/// 重构后经 ExecuteStepListAsync 递归解释器转绿。
/// </summary>
public class TestSuiteEngineInterpreterTests
{
    // ── golden 基线 suite：覆盖 for 循环体全部分支（Comment/executor pass/executor fail/
    //    负测试 A/B/no-executor/executor-throws/帧捕获）。ContinueAll 让所有步骤跑完。──

    private const uint Frame1Id = 0x101;
    private const uint Frame2Id = 0x102;

    /// <summary>golden suite 的脚本化 executor：call0 pass / call1 fail / call2 fail(→neg A) / call3 pass(→neg B) / call4 throw。</summary>
    private static ScriptedStepExecutor BuildGoldenScriptedExecutor() => new(
        TestCaseStepKind.AssertSignal,
        new StepResult(0, TestCaseStepKind.AssertSignal, null, StepStatus.Passed, "sig pass", null, null, 0),
        new StepResult(0, TestCaseStepKind.AssertSignal, null, StepStatus.Failed, "sig fail", null, null, 0),
        new StepResult(0, TestCaseStepKind.AssertSignal, null, StepStatus.Failed, "sig negated fail", null, null, 0),
        new StepResult(0, TestCaseStepKind.AssertSignal, null, StepStatus.Passed, "sig negated pass", null, null, 0))
    { ThrowOnCallIndex = 4, ExceptionToThrow = new InvalidOperationException("boom") };

    /// <summary>构造带 2 帧的 golden 上下文（IHasRecentFrames）。</summary>
    private static GoldenAssertionContext BuildGoldenContext()
    {
        var ctx = new GoldenAssertionContext();
        ctx.PushFrame(new CanFrame(new CanId(Frame1Id, FrameFormat.Standard), new byte[] { 0x01 }, FrameFlags.None, default, default));
        ctx.PushFrame(new CanFrame(new CanId(Frame2Id, FrameFormat.Standard), new byte[] { 0x02 }, FrameFlags.None, default, default));
        return ctx;
    }

    // ── golden 文件位置（源码树，随仓库提交）──

    private static string FindProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && dir.GetFiles("*.csproj").Length == 0)
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    private static string GoldenFilePath => System.IO.Path.Combine(FindProjectRoot(), "HIL", "Golden", "noncontrolflow-baseline.json");

    // ── golden DTO + 序列化（只存语义字段 + 帧计数/帧 ID，不存 CanFrame 全字段）──

    private sealed record GoldenStepDto(int StepIndex, string Status, string? Message, int FrameCount, int[] FrameIds, string? Path, int? Iteration);

    private static GoldenStepDto[] ToGolden(IReadOnlyList<StepResult> results)
    {
        var dtos = new GoldenStepDto[results.Count];
        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            var frames = r.FramesAroundFailure;
            dtos[i] = new GoldenStepDto(
                r.StepIndex,
                r.Status.ToString(),
                r.Message,
                frames?.Count ?? 0,
                frames?.Select(f => (int)f.Id.Raw).ToArray() ?? Array.Empty<int>(),
                r.Path,
                r.Iteration);
        }
        return dtos;
    }

    private static readonly System.Text.Json.JsonSerializerOptions GoldenJsonOptions = new() { WriteIndented = true };

    private static List<StepResult> FromGolden(GoldenStepDto[] dtos) =>
        dtos.Select(d => new StepResult(
            d.StepIndex,
            TestCaseStepKind.Comment,  // Kind 不在比对口径内，占位
            null,
            Enum.Parse<StepStatus>(d.Status),
            d.Message,
            null, null, 0,
            // 帧只比计数 + ID（语义等价证明，帧内容由 FramesAroundFailureTests 独立覆盖）
            d.FrameCount == 0 ? null : d.FrameIds.Select(id => new CanFrame(
                new CanId((uint)id, id > 0x7FF ? FrameFormat.Extended : FrameFormat.Standard),
                Array.Empty<byte>(), FrameFlags.None, default, default)).ToList(),
            d.Path,
            d.Iteration)).ToList();

    private static void WriteGolden(GoldenStepDto[] dtos)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(GoldenFilePath)!);
        var json = System.Text.Json.JsonSerializer.Serialize(dtos, GoldenJsonOptions);
        File.WriteAllText(GoldenFilePath, json);
    }

    private static GoldenStepDto[] ReadGolden()
    {
        if (!File.Exists(GoldenFilePath))
            throw new FileNotFoundException("Golden baseline missing; run RegenerateGoldenBaseline once.", GoldenFilePath);
        var json = File.ReadAllText(GoldenFilePath);
        return System.Text.Json.JsonSerializer.Deserialize<GoldenStepDto[]>(json)!
            ?? throw new InvalidOperationException("Golden baseline empty.");
    }

    // v11.1 ④：比对器口径写死——只断言语义三字段 + 非控制流 Path/Iteration
    private static void AssertStepResultsSemanticEquivalent(List<StepResult> before, IReadOnlyList<StepResult> after)
    {
        after.Should().HaveCount(before.Count);
        foreach (var (b, a) in before.Zip(after))
        {
            a.Status.Should().Be(b.Status, "step {0} status", b.StepIndex);
            a.Message.Should().Be(b.Message, "step {0} message", b.StepIndex);
            // 帧语义等价：计数 + 顺序 + ID
            var af = (a.FramesAroundFailure ?? Array.Empty<CanFrame>()).Select(f => f.Id.Raw).ToList();
            var bf = (b.FramesAroundFailure ?? Array.Empty<CanFrame>()).Select(f => f.Id.Raw).ToList();
            af.Should().BeEquivalentTo(bf, "step {0} frames", b.StepIndex);
            a.Path.Should().BeNull();      // 非控制流 suite，重构前后 Path 都为 null
            a.Iteration.Should().BeNull();
        }
    }

    // ── Step 1: golden 基线生成（重构前跑一次，随后 Skip 防止覆盖）──

    [Fact(Skip = "手动跑一次以重建 golden 基线：临时去掉 Skip，运行，再恢复 Skip。重构前生成。")]
    public async Task RegenerateGoldenBaseline()
    {
        var engine = CreateEngine(BuildGoldenScriptedExecutor());
        var suite = MakeGoldenSuiteCase();
        var ctx = BuildGoldenContext();

        var result = await engine.ExecuteAsync(suite, ctx, new TestSuiteConfig(), null, default);
        WriteGolden(ToGolden(result.CaseResults[0].StepResults));
    }

    // ── Step 1b: golden 比对（重构前生成后绿，重构后保持绿 = 语义等价证明）──

    [Fact]
    public async Task NonControlFlowSuite_MatchesGoldenBaseline()
    {
        var engine = CreateEngine(BuildGoldenScriptedExecutor());
        var suite = MakeGoldenSuiteCase();
        var ctx = BuildGoldenContext();

        var result = await engine.ExecuteAsync(suite, ctx, new TestSuiteConfig(), null, default);

        var golden = FromGolden(ReadGolden());
        AssertStepResultsSemanticEquivalent(golden, result.CaseResults[0].StepResults);
    }

    /// <summary>构造 golden suite 的 case（与生成路径同构：Comment/pass/fail/negA/negB/no-exec/throw 7 步）。</summary>
    private static TestSuite MakeGoldenSuiteCase()
    {
        var step0 = TestCaseStep.Create(new CommentStep("doc"));
        var step1 = TestCaseStep.Create(new AssertSignalStep("RPM", 3000.0, 10.0));
        var step2 = TestCaseStep.Create(new AssertSignalStep("RPM", 3000.0, 10.0));
        var step3 = TestCaseStep.Create(new AssertSignalStep("RPM", 3000.0, 10.0), expectedVerdict: ExpectedVerdict.Fail);
        var step4 = TestCaseStep.Create(new AssertSignalStep("RPM", 3000.0, 10.0), expectedVerdict: ExpectedVerdict.Fail);
        var step5 = TestCaseStep.Create(new DelayStep(50));          // 无 executor → No executor for kind Delay
        var step6 = TestCaseStep.Create(new AssertSignalStep("RPM", 3000.0, 10.0)); // 抛异常
        return new TestSuite("GoldenBaseline", new[] { CreateCase(step0, step1, step2, step3, step4, step5, step6) },
            Array.Empty<string>(), Array.Empty<string>(), new TestSuiteConfig(), 0);
    }

    // ── 共享 helper（对齐 TestSuiteEngineTests 的 CreateEngine/CreateCase）──

    private static TestSuiteEngine CreateEngine(params IStepExecutor[] executors)
    {
        var fixtureResolver = new FakeFixtureResolver();
        return new TestSuiteEngine(fixtureResolver, executors);
    }

    private static TestCase CreateCase(params TestCaseStep[] steps) => new(
        Id: "case_1", Name: "Test Case", Description: "",
        PreConditions: null, Steps: steps, PostConditions: null,
        Tags: Array.Empty<string>(), TimeoutMs: 0, CaseFixtureKeys: null);

    // ── suite 级参数注入（fix 验证）：ExecuteAsync 应把 suite.Parameters 注入
    //    StepScope.SuiteParams 层，使 ${suite_param} 经 Resolve 命中。
    //    修复前 ExecuteCaseAsync suiteParams=null → ${turnMs}→Undefined（假通过）。──

    [Fact]
    public async Task SuiteParameters_InjectedIntoScope_AssignResolvesSuiteParam()
    {
        var engine = CreateEngine();
        var assignStep = TestCaseStep.Create(new AssignStep("x", "${turnMs}"));
        var suiteParams = new Dictionary<string, ParameterValue> { ["turnMs"] = new(ParameterKind.Number, 200.0) };
        var suite = new TestSuite("S", new[] { CreateCase(assignStep) },
            Array.Empty<string>(), Array.Empty<string>(), new TestSuiteConfig(), 0,
            Parameters: suiteParams);
        var ctx = new StoreBackedAssertionContext();

        var result = await engine.ExecuteAsync(suite, ctx, new TestSuiteConfig(), null, default);

        var assignResult = result.CaseResults[0].StepResults[0];
        assignResult.Status.Should().Be(StepStatus.Passed);
        assignResult.ActualValue.Should().Contain("200", "turnMs should resolve to suite param, not Undefined");
        ctx.Variables["x"].Should().Be(200.0);
    }

    // ── 控制流用例（重构前红：引擎返回 No executor for kind If/Repeat/Loop/Assign；
    //    重构后绿：ExecuteStepListAsync 递归解释）──

    [Fact]
    public async Task If_TrueCondition_RunsBodyStep()
    {
        var exec = new FakeStepExecutor(TestCaseStepKind.AssertSignal)
        {
            Result = new StepResult(0, TestCaseStepKind.AssertSignal, null, StepStatus.Passed, "body ran", null, null, 0),
        };
        var engine = CreateEngine(exec);
        var bodyStep = TestCaseStep.Create(new AssertSignalStep("RPM", 3000.0, 10.0));
        var ifStep = TestCaseStep.Create(new IfStep("1 == 1", new[] { bodyStep }, null));
        var suite = new TestSuite("S", new[] { CreateCase(ifStep) },
            Array.Empty<string>(), Array.Empty<string>(), new TestSuiteConfig(), 0);

        var result = await engine.ExecuteAsync(suite, new StoreBackedAssertionContext(), new TestSuiteConfig(), null, default);

        // body 步骤确实被执行
        exec.ExecuteCallCount.Should().Be(1);
        var steps = result.CaseResults[0].StepResults;
        steps.Should().Contain(s => s.Status == StepStatus.Passed && s.Message == "body ran");
    }

    [Fact]
    public async Task If_FalseCondition_RunsElseBodyStep()
    {
        var exec = new FakeStepExecutor(TestCaseStepKind.AssertSignal)
        {
            Result = new StepResult(0, TestCaseStepKind.AssertSignal, null, StepStatus.Passed, "else ran", null, null, 0),
        };
        var engine = CreateEngine(exec);
        var bodyStep = TestCaseStep.Create(new AssertSignalStep("RPM", 3000.0, 10.0));
        var elseStep = TestCaseStep.Create(new AssertSignalStep("RPM", 3000.0, 10.0));
        var ifStep = TestCaseStep.Create(new IfStep("1 == 2", new[] { bodyStep }, new[] { elseStep }));
        var suite = new TestSuite("S", new[] { CreateCase(ifStep) },
            Array.Empty<string>(), Array.Empty<string>(), new TestSuiteConfig(), 0);

        var result = await engine.ExecuteAsync(suite, new StoreBackedAssertionContext(), new TestSuiteConfig(), null, default);

        // 条件 false → 只跑 else 分支
        exec.ExecuteCallCount.Should().Be(1);
        var steps = result.CaseResults[0].StepResults;
        steps.Should().Contain(s => s.Message == "else ran");
        steps.Should().NotContain(s => s.Message == "body ran");
    }

    [Fact]
    public async Task If_UndefinedReference_TreatedAsFalse_GoesElse()
    {
        var exec = new FakeStepExecutor(TestCaseStepKind.AssertSignal)
        {
            Result = new StepResult(0, TestCaseStepKind.AssertSignal, null, StepStatus.Passed, "else ran", null, null, 0),
        };
        var engine = CreateEngine(exec);
        var elseStep = TestCaseStep.Create(new AssertSignalStep("RPM", 3000.0, 10.0));
        // 单引用 undefined → falsy + warning（§5.5）
        var ifStep = TestCaseStep.Create(new IfStep("${missing}", Array.Empty<TestCaseStep>(), new[] { elseStep }));
        var suite = new TestSuite("S", new[] { CreateCase(ifStep) },
            Array.Empty<string>(), Array.Empty<string>(), new TestSuiteConfig(), 0);

        var result = await engine.ExecuteAsync(suite, new StoreBackedAssertionContext(), new TestSuiteConfig(), null, default);

        exec.ExecuteCallCount.Should().Be(1, "undefined 引用走 else 分支");
        var container = result.CaseResults[0].StepResults[0];
        container.Status.Should().Be(StepStatus.Passed);
        container.Message.Should().Contain("undefined");
    }

    [Fact]
    public async Task Repeat_Fixed_RunsBodyNTimes()
    {
        var exec = new FakeStepExecutor(TestCaseStepKind.AssertSignal)
        {
            Result = new StepResult(0, TestCaseStepKind.AssertSignal, null, StepStatus.Passed, "iter", null, null, 0),
        };
        var engine = CreateEngine(exec);
        var bodyStep = TestCaseStep.Create(new AssertSignalStep("RPM", 3000.0, 10.0));
        var repeatStep = TestCaseStep.Create(new RepeatStep(RepeatMode.Fixed, Count: "3", Condition: null, Body: new[] { bodyStep }, MaxIterations: 100));
        var suite = new TestSuite("S", new[] { CreateCase(repeatStep) },
            Array.Empty<string>(), Array.Empty<string>(), new TestSuiteConfig(), 0);

        var result = await engine.ExecuteAsync(suite, new StoreBackedAssertionContext(), new TestSuiteConfig(), null, default);

        exec.ExecuteCallCount.Should().Be(3, "Fixed=3 应执行 body 3 次");
        // 3 个 body 叶 StepResult + 1 个容器 StepResult
        var leafSteps = result.CaseResults[0].StepResults.Where(r => r.Message == "iter").ToList();
        leafSteps.Should().HaveCount(3);
        // Iteration 0/1/2
        leafSteps[0].Iteration.Should().Be(0);
        leafSteps[1].Iteration.Should().Be(1);
        leafSteps[2].Iteration.Should().Be(2);
    }

    [Fact]
    public async Task Repeat_While_StopsWhenConditionFalse()
    {
        var exec = new FakeStepExecutor(TestCaseStepKind.AssertSignal)
        {
            Result = new StepResult(0, TestCaseStepKind.AssertSignal, null, StepStatus.Passed, "iter", null, null, 0),
        };
        var engine = CreateEngine(exec);
        // body: Assign(i = ${i} + 1) 让 ${i} 自增；初始 i=0（由前置 Assign 提供）
        var assignStep = TestCaseStep.Create(new AssignStep("i", "${i} + 1"));
        var bodyStep = TestCaseStep.Create(new AssertSignalStep("RPM", 3000.0, 10.0));
        var repeatStep = TestCaseStep.Create(new RepeatStep(
            RepeatMode.While, Count: null, Condition: "${i} < 3", Body: new[] { assignStep, bodyStep }, MaxIterations: 100));
        var initStep = TestCaseStep.Create(new AssignStep("i", "0"));
        var suite = new TestSuite("S", new[] { CreateCase(initStep, repeatStep) },
            Array.Empty<string>(), Array.Empty<string>(), new TestSuiteConfig(), 0);

        var result = await engine.ExecuteAsync(suite, new StoreBackedAssertionContext(), new TestSuiteConfig(), null, default);

        // i: 0(跑,→1)→1(跑,→2)→2(跑,→3)→3(guard 假,停)。body 跑 3 次
        exec.ExecuteCallCount.Should().Be(3, "i<3 在 i=0/1/2 时真，i=3 时 guard 假");
        var leafSteps = result.CaseResults[0].StepResults.Where(r => r.Message == "iter").ToList();
        leafSteps.Should().HaveCount(3);
    }

    [Fact]
    public async Task Loop_Range_RunsBodyForEachValue()
    {
        var exec = new FakeStepExecutor(TestCaseStepKind.AssertSignal)
        {
            Result = new StepResult(0, TestCaseStepKind.AssertSignal, null, StepStatus.Passed, "loop body", null, null, 0),
        };
        var engine = CreateEngine(exec);
        var bodyStep = TestCaseStep.Create(new AssertSignalStep("RPM", 3000.0, 10.0));
        var loopStep = TestCaseStep.Create(new LoopStep(From: "1", To: "3", Step: "1", Body: new[] { bodyStep }, IndexVar: "v"));
        var suite = new TestSuite("S", new[] { CreateCase(loopStep) },
            Array.Empty<string>(), Array.Empty<string>(), new TestSuiteConfig(), 0);

        var result = await engine.ExecuteAsync(suite, new StoreBackedAssertionContext(), new TestSuiteConfig(), null, default);

        exec.ExecuteCallCount.Should().Be(3, "Loop 1..3 含端点 → 3 次");
        var leafSteps = result.CaseResults[0].StepResults.Where(r => r.Message == "loop body").ToList();
        leafSteps.Should().HaveCount(3);
    }

    [Fact]
    public async Task Assign_WritesVariable_ReadableBySubsequentIf()
    {
        var exec = new FakeStepExecutor(TestCaseStepKind.AssertSignal)
        {
            Result = new StepResult(0, TestCaseStepKind.AssertSignal, null, StepStatus.Passed, "branch ran", null, null, 0),
        };
        var engine = CreateEngine(exec);
        var assignStep = TestCaseStep.Create(new AssignStep("x", "42"));
        var bodyStep = TestCaseStep.Create(new AssertSignalStep("RPM", 3000.0, 10.0));
        var ifStep = TestCaseStep.Create(new IfStep("${x} > 10", new[] { bodyStep }, null));
        var suite = new TestSuite("S", new[] { CreateCase(assignStep, ifStep) },
            Array.Empty<string>(), Array.Empty<string>(), new TestSuiteConfig(), 0);

        var result = await engine.ExecuteAsync(suite, new StoreBackedAssertionContext(), new TestSuiteConfig(), null, default);

        // x=42 > 10 → 条件真 → body 跑一次
        exec.ExecuteCallCount.Should().Be(1);
        var assignResult = result.CaseResults[0].StepResults[0];
        assignResult.Status.Should().Be(StepStatus.Passed);
        assignResult.Message.Should().Contain("Assigned x");
    }

    [Fact]
    public async Task StepIndex_SharedAcrossIfBody_TopLevelLeavesNullPath()
    {
        // 顶层 [If(index=1, body=[leaf0, leaf1]), leaf2]
        // body 叶共享 If 的 StepIndex=1；顶层叶 Path=null；body 叶 Path="1.0"/"1.1"
        var exec = new FakeStepExecutor(TestCaseStepKind.AssertSignal)
        {
            Result = new StepResult(0, TestCaseStepKind.AssertSignal, null, StepStatus.Passed, "leaf", null, null, 0),
        };
        var engine = CreateEngine(exec);
        var body0 = TestCaseStep.Create(new AssertSignalStep("RPM", 3000.0, 10.0));
        var body1 = TestCaseStep.Create(new AssertSignalStep("RPM", 3000.0, 10.0));
        var ifStep = TestCaseStep.Create(new IfStep("1 == 1", new[] { body0, body1 }, null));
        var topLeaf = TestCaseStep.Create(new AssertSignalStep("RPM", 3000.0, 10.0));
        var suite = new TestSuite("S", new[] { CreateCase(ifStep, topLeaf) },
            Array.Empty<string>(), Array.Empty<string>(), new TestSuiteConfig(), 0);

        var result = await engine.ExecuteAsync(suite, new StoreBackedAssertionContext(), new TestSuiteConfig(), null, default);
        var steps = result.CaseResults[0].StepResults;

        // 容器 If 在 index 0：StepIndex=0, Path=null（顶层）
        var container = steps[0];
        container.StepIndex.Should().Be(0);
        container.Path.Should().BeNull();

        // body 叶：StepIndex=0（共享），Path="0.0"/"0.1"（顶层 leaf Path=null，用 Path 区分）
        var bodyLeaves = steps.Where(s => s.Path is not null && s.Path.StartsWith("0.", StringComparison.Ordinal)).ToList();
        bodyLeaves.Should().HaveCount(2);
        bodyLeaves[0].StepIndex.Should().Be(0, "body 叶共享外层 If 的 StepIndex");
        bodyLeaves[0].Path.Should().Be("0.0");
        bodyLeaves[1].Path.Should().Be("0.1");

        // 顶层 leaf：StepIndex=1, Path=null
        var topLeafResult = steps[^1];
        topLeafResult.StepIndex.Should().Be(1);
        topLeafResult.Path.Should().BeNull();
    }

    [Fact]
    public async Task StopCaseOnFailure_InTopLevel_SkipsRemainingTopLevel()
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

        var result = await engine.ExecuteAsync(suite, new StoreBackedAssertionContext(),
            new TestSuiteConfig(FailurePolicy.StopCaseOnFailure), null, default);

        var steps = result.CaseResults[0].StepResults;
        steps[0].Status.Should().Be(StepStatus.Failed);
        steps[1].Status.Should().Be(StepStatus.Skipped);
    }

    [Fact]
    public async Task Repeat_MaxIterations_Guard_FailsWhenExceeded()
    {
        var exec = new FakeStepExecutor(TestCaseStepKind.AssertSignal)
        {
            Result = new StepResult(0, TestCaseStepKind.AssertSignal, null, StepStatus.Passed, "iter", null, null, 0),
        };
        var engine = CreateEngine(exec);
        var bodyStep = TestCaseStep.Create(new AssertSignalStep("RPM", 3000.0, 10.0));
        // Fixed 请求 5 次，MaxIterations=2 → 超 MaxIterations → 容器 Failed
        var repeatStep = TestCaseStep.Create(new RepeatStep(RepeatMode.Fixed, Count: "5", Condition: null, Body: new[] { bodyStep }, MaxIterations: 2));
        var suite = new TestSuite("S", new[] { CreateCase(repeatStep) },
            Array.Empty<string>(), Array.Empty<string>(), new TestSuiteConfig(), 0);

        var result = await engine.ExecuteAsync(suite, new StoreBackedAssertionContext(), new TestSuiteConfig(), null, default);

        result.CaseResults[0].Passed.Should().BeFalse();
        var container = result.CaseResults[0].StepResults[0];
        container.Status.Should().Be(StepStatus.Failed);
        container.Message.Should().Contain("MaxIterations");
    }

    // ── Fakes ──

    /// <summary>脚本化 executor：按调用序号返回预设结果；ThrowOnCallIndex 命中时抛异常。</summary>
    private sealed class ScriptedStepExecutor : IStepExecutor
    {
        private readonly StepResult[] _results;
        private int _call;
        public TestCaseStepKind Kind { get; }
        public int? ThrowOnCallIndex { get; set; }
        public Exception? ExceptionToThrow { get; set; }

        public ScriptedStepExecutor(TestCaseStepKind kind, params StepResult[] results)
        {
            Kind = kind;
            _results = results;
        }

        public Task<StepResult> ExecuteAsync(TestCaseStep step, IAssertionContext ctx, CancellationToken ct)
        {
            var idx = _call++;
            if (ThrowOnCallIndex == idx && ExceptionToThrow is not null)
                return Task.FromException<StepResult>(ExceptionToThrow);
            var r = idx < _results.Length ? _results[idx] : _results[^1];
            return Task.FromResult(r with { Kind = step.Kind, StepIndex = 0, ElapsedMs = 0 });
        }
    }

    /// <summary>支持 IStepVariableStore（Assign 读写）+ IHasRecentFrames（帧捕获）的上下文。</summary>
    private sealed class StoreBackedAssertionContext : IAssertionContext, IStepVariableStore, IHasRecentFrames
    {
        public IDictionary<string, object> Variables { get; } = new Dictionary<string, object>();
        public double CurrentTimestamp => 0;
        public IReadOnlyList<DecodedFrame> GetRecentDecodedFrames() => Array.Empty<DecodedFrame>();
        public IDisposable SubscribeDecodedFrames(Action<DecodedFrame> onFrame) => new NopDisposable();
        public double? GetSignalValue(string signalName, int maxAgeMs = 5000) => null;
        public ValueTask<Result<Unit>> SendFrameAsync(CanFrame frame, CancellationToken ct) => ValueTask.FromResult(Result<Unit>.Ok(default));
        public IReadOnlyList<CanFrame> GetRecentFrames() => Array.Empty<CanFrame>();
        private sealed class NopDisposable : IDisposable { public void Dispose() { } }
    }

    /// <summary>golden 基线用：IHasRecentFrames 返回 push 进来的帧。</summary>
    private sealed class GoldenAssertionContext : IAssertionContext, IStepVariableStore, IHasRecentFrames
    {
        private readonly List<CanFrame> _recent = new();
        public IDictionary<string, object> Variables { get; } = new Dictionary<string, object>();
        public double CurrentTimestamp => 0;
        public IReadOnlyList<DecodedFrame> GetRecentDecodedFrames() => Array.Empty<DecodedFrame>();
        public IDisposable SubscribeDecodedFrames(Action<DecodedFrame> onFrame) => new NopDisposable();
        public double? GetSignalValue(string signalName, int maxAgeMs = 5000) => null;
        public ValueTask<Result<Unit>> SendFrameAsync(CanFrame frame, CancellationToken ct) => ValueTask.FromResult(Result<Unit>.Ok(default));
        public IReadOnlyList<CanFrame> GetRecentFrames() => _recent.AsReadOnly();
        public void PushFrame(CanFrame frame) => _recent.Add(frame);
        private sealed class NopDisposable : IDisposable { public void Dispose() { } }
    }
}
