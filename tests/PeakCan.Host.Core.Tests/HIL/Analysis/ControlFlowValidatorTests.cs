using FluentAssertions;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Analysis;
using PeakCan.HIL.Core.HIL.Expressions;

namespace PeakCan.HIL.Core.Tests.HIL.Analysis;

/// <summary>
/// B.4 控制流校验器测试（§5.8 规则 ①-⑥ + 语法）。
/// 覆盖 brief 必测 9 用例 + 正向回归。
/// </summary>
public class ControlFlowValidatorTests
{
    private readonly StepValidatorRegistry _registry = new(new ExpressionEvaluator());

    /// <summary>构造单 case suite（顶层 steps）。</summary>
    private static TestSuite BuildSuite(params TestCaseStep[] steps)
    {
        var testCase = new TestCase("c1", "case1", "", null, steps, null, Array.Empty<string>());
        return new TestSuite("s1", new[] { testCase }, Array.Empty<string>(), Array.Empty<string>(), new TestSuiteConfig());
    }

    // ── ① while 守卫引用循环体内首次写入变量 → Critical ──

    [Fact]
    public void WhileGuard_ReferencesVariableFirstWrittenInBody_ReportsCritical_1()
    {
        // Arrange: Repeat While 守卫引用 ${state}，state 首次写入在 body 内 Assign（无前置 writer）
        var body = new[]
        {
            TestCaseStep.Create(new AssignStep("state", "0")),
        };
        var repeat = new RepeatStep(RepeatMode.While, Count: null, Condition: "${state}", body, MaxIterations: "100");
        var suite = BuildSuite(TestCaseStep.Create(repeat));

        // Act
        var issues = _registry.Validate(suite);

        // Assert: ① Critical — 守卫引用必然 undefined（首次写入在循环体内，§5.8 ①，v11 M7 load-bearing）
        issues.Should().Contain(
            i => i.RuleId == "①" && i.Severity == ValidationSeverity.Critical,
            "while guard references state whose first writer is inside the loop body (§5.8 ①)");
    }

    // ── ② did 引用无前置 writer → Critical ──

    [Fact]
    public void DidReference_NoPrecedingWriter_ReportsCritical_2()
    {
        // Arrange: IfStep 守卫引用 did.0xF190，case 内无 ReadDid 前置 writer
        var body = new[]
        {
            TestCaseStep.Create(new CommentStep("noop")),
        };
        var ifStep = new IfStep("did.0xF190 == 1", body, ElseBody: null);
        var suite = BuildSuite(TestCaseStep.Create(ifStep));

        // Act
        var issues = _registry.Validate(suite);

        // Assert: ② Critical — did 引用必然 undefined（case 内无前置 writer）
        issues.Should().Contain(
            i => i.RuleId == "②" && i.Severity == ValidationSeverity.Critical,
            "did.0xF190 reference has no preceding writer in case (§5.8 ②)");
    }

    // ── ⑥ AssignStep.Assign 与 IndexVar 同名 → Critical ──

    [Fact]
    public void AssignStep_AssignSameAsLoopIndexVar_ReportsCritical_6()
    {
        // Arrange: Loop(IndexVar="i") + body Assign(Assign="i") — 写入被 loop 绑定遮蔽
        var body = new[]
        {
            TestCaseStep.Create(new AssignStep("i", "0")),
        };
        var loop = new LoopStep(From: "1", To: "10", Step: "1", body, IndexVar: "i");
        var suite = BuildSuite(TestCaseStep.Create(loop));

        // Act
        var issues = _registry.Validate(suite);

        // Assert: ⑥ Critical — Assign "i" 与 IndexVar "i" 同名（写入被循环绑定遮蔽）
        issues.Should().Contain(
            i => i.RuleId == "⑥" && i.Severity == ValidationSeverity.Critical,
            "AssignStep.Assign 'i' shadows loop IndexVar 'i' (§5.8 ⑥)");
    }

    // ── ④ 容器 ExpectedVerdict ≠ Any → Critical ──

    [Fact]
    public void Container_ExpectedVerdictNotAny_ReportsCritical_4()
    {
        // Arrange: IfStep 容器设 ExpectedVerdict=Fail（≠Any）
        var ifStep = new IfStep("true", new[] { TestCaseStep.Create(new CommentStep("x")) }, null);
        var step = TestCaseStep.Create(ifStep, expectedVerdict: ExpectedVerdict.Fail);
        var suite = BuildSuite(step);

        // Act
        var issues = _registry.Validate(suite);

        // Assert: ④ Critical — 容器 ExpectedVerdict ≠ Any
        issues.Should().Contain(
            i => i.RuleId == "④" && i.Severity == ValidationSeverity.Critical,
            "container IfStep has ExpectedVerdict=Fail (§5.8 ④)");
    }

    // ── ⑤a Loop.Step ≤ 0 → Critical ──

    [Fact]
    public void Loop_StepLEZero_ReportsCritical_5a()
    {
        // Arrange: Loop Step="0"（常量 ≤0）
        var body = new[] { TestCaseStep.Create(new CommentStep("x")) };
        var loop = new LoopStep("1", "10", "0", body);
        var suite = BuildSuite(TestCaseStep.Create(loop));

        // Act
        var issues = _registry.Validate(suite);

        // Assert: ⑤a Critical — Loop.Step=0 <= 0
        issues.Should().Contain(
            i => i.RuleId == "⑤a" && i.Severity == ValidationSeverity.Critical,
            "Loop.Step=0 <= 0 (§5.8 ⑤a)");
    }

    // ── ⑤b MaxIterations 越界 → Critical ──

    [Fact]
    public void Repeat_MaxIterationsOutOfBounds_ReportsCritical_5b()
    {
        // Arrange: Repeat Fixed MaxIterations=0（<1）
        var body = new[] { TestCaseStep.Create(new CommentStep("x")) };
        var repeat = new RepeatStep(RepeatMode.Fixed, Count: "1", Condition: null, body, MaxIterations: "0");
        var suite = BuildSuite(TestCaseStep.Create(repeat));

        // Act
        var issues = _registry.Validate(suite);

        // Assert: ⑤b Critical — MaxIterations=0 < 1
        issues.Should().Contain(
            i => i.RuleId == "⑤b" && i.Severity == ValidationSeverity.Critical,
            "Repeat.MaxIterations=0 < 1 (§5.8 ⑤b)");
    }

    // ── 语法 表达式解析失败 → Critical ──

    [Fact]
    public void ExpressionSyntaxError_ReportsCritical_Syntax()
    {
        // Arrange: IfStep condition="1 +"（解析失败）
        var body = new[] { TestCaseStep.Create(new CommentStep("x")) };
        var ifStep = new IfStep("1 +", body, null);
        var suite = BuildSuite(TestCaseStep.Create(ifStep));

        // Act
        var issues = _registry.Validate(suite);

        // Assert: 语法 Critical — condition parse error
        issues.Should().Contain(
            i => i.RuleId == "语法" && i.Severity == ValidationSeverity.Critical,
            "IfStep condition '1 +' parse error (§5.8 syntax)");
    }

    // ── 逃生舱 isUndefined → ① 不升级 Critical ──

    [Fact]
    public void WhileGuard_WithIsUndefinedEscape_NoCritical_7()
    {
        // Arrange: Repeat While 守卫含 isUndefined(${state})，state 无前置 writer
        var body = new[]
        {
            TestCaseStep.Create(new AssignStep("state", "0")),
        };
        var repeat = new RepeatStep(RepeatMode.While, null, "isUndefined(${state})", body, "100");
        var suite = BuildSuite(TestCaseStep.Create(repeat));

        // Act
        var issues = _registry.Validate(suite);

        // Assert: ① 不升级 Critical（逃生舱识别 isUndefined）
        issues.Should().NotContain(
            i => i.RuleId == "①" && i.Severity == ValidationSeverity.Critical,
            "isUndefined escape hatch suppresses ① Critical (§5.8 escape)");
    }

    // ── ⑤c 嵌套深度 > 10 → Medium ──

    [Fact]
    public void NestingDepthGreaterThan10_ReportsMedium_5c()
    {
        // Arrange: 12 层嵌套 If（最深 depth=11 > 10）
        TestCaseStep BuildNested(int depth) =>
            depth <= 0
                ? TestCaseStep.Create(new CommentStep("leaf"))
                : TestCaseStep.Create(new IfStep("true", new[] { BuildNested(depth - 1) }, null));

        var suite = BuildSuite(BuildNested(12));

        // Act
        var issues = _registry.Validate(suite);

        // Assert: ⑤c Medium（不拦运行）
        issues.Should().Contain(
            i => i.RuleId == "⑤c" && i.Severity == ValidationSeverity.Medium,
            "nesting depth 11 > 10 (§5.8 ⑤c)");
    }

    // ── 正向：前置 writer 存在 → 无 ② issue ──

    [Fact]
    public void PrecedingWriter_Exists_NoDidIssue_9()
    {
        // Arrange: ReadDid(0xF190) 在 IfStep(did.0xF190==1) 之前 → did_0xF190 已写入
        var steps = new[]
        {
            TestCaseStep.Create(new ReadDidStep(0xF190)),
            TestCaseStep.Create(new IfStep("did.0xF190 == 1",
                new[] { TestCaseStep.Create(new CommentStep("x")) }, null)),
        };
        var suite = BuildSuite(steps);

        // Act
        var issues = _registry.Validate(suite);

        // Assert: 无 ② issue（did_0xF190 有前置 writer）
        issues.Should().NotContain(
            i => i.RuleId == "②",
            "did.0xF190 has preceding ReadDid writer (no ② issue)");
    }

    // ── 正向：干净 suite 无 issue ──

    [Fact]
    public void CleanSuite_ReportsNoIssues()
    {
        // Arrange: ReadDid 前置 + If 引用 + body Assign（无悬空/遮蔽）
        var steps = new[]
        {
            TestCaseStep.Create(new ReadDidStep(0xF190)),
            TestCaseStep.Create(new IfStep("did.0xF190 == 1",
                new[] { TestCaseStep.Create(new AssignStep("x", "1")) }, null)),
        };
        var suite = BuildSuite(steps);

        // Act
        var issues = _registry.Validate(suite);

        // Assert: 干净 suite 无任何 issue
        issues.Should().BeEmpty("clean suite with valid control flow has no issues");
    }

    // ── ⑤a Loop.Step 为 hex 字面量 0 → Critical（HexLiteral 覆盖） ──

    [Fact]
    public void Loop_StepHexLiteralZero_ReportsCritical_5a()
    {
        // Arrange: Loop Step="0x0"（hex 字面量 0，≤0）
        var body = new[] { TestCaseStep.Create(new CommentStep("x")) };
        var loop = new LoopStep("1", "10", "0x0", body);
        var suite = BuildSuite(TestCaseStep.Create(loop));

        // Act
        var issues = _registry.Validate(suite);

        // Assert: ⑤a Critical — Hex literal 0 <= 0
        issues.Should().Contain(
            i => i.RuleId == "⑤a" && i.Severity == ValidationSeverity.Critical,
            "Loop Step=0x0 (hex literal 0) <= 0 (§5.8 ⑤a)");
    }

    // ── ①′ while 守卫引用 If 分支内 writer → High（可能 undefined） ──

    [Fact]
    public void WhileGuard_ReferenceWrittenInIfBody_ReportsHigh_1Prime()
    {
        // Arrange: If body assign x（条件 writer）→ 后续 Repeat While(x>0) 守卫引用 x
        var ifBody = new[] { TestCaseStep.Create(new AssignStep("x", "1")) };
        var ifStep = new IfStep("true", ifBody, null);
        var repeat = new RepeatStep(RepeatMode.While, null, "${x}", Array.Empty<TestCaseStep>(), "100");
        var steps = new[]
        {
            TestCaseStep.Create(ifStep),
            TestCaseStep.Create(repeat),
        };
        var suite = BuildSuite(steps);

        // Act
        var issues = _registry.Validate(suite);

        // Assert: ①′ High — 守卫引用可能 undefined（writer 在 If 分支内，可能不执行）
        issues.Should().Contain(
            i => i.RuleId == "①′" && i.Severity == ValidationSeverity.High,
            "while guard references 'x' written only in If body (may be undefined, §5.8 ①′)");
    }

    // ── ⑥ 嵌套 IndexVar 遮蔽外层 → Critical ──

    [Fact]
    public void AssignStep_ShadowsOuterIndexVarInNestedLoop_ReportsCritical_6()
    {
        // Arrange: 外层 Loop(i) > 内层 Loop(j) > body Assign(i) — i 是外层 IndexVar
        var innerBody = new[]
        {
            TestCaseStep.Create(new AssignStep("i", "0")),
        };
        var innerLoop = new LoopStep("1", "3", "1", innerBody, IndexVar: "j");
        var outerLoop = new LoopStep("1", "3", "1", new[] { TestCaseStep.Create(innerLoop) }, IndexVar: "i");
        var suite = BuildSuite(TestCaseStep.Create(outerLoop));

        // Act
        var issues = _registry.Validate(suite);

        // Assert: ⑥ Critical — Assign "i" 遮蔽外层 IndexVar "i"（嵌套作用域）
        issues.Should().Contain(
            i => i.RuleId == "⑥" && i.Severity == ValidationSeverity.Critical,
            "AssignStep.Assign 'i' shadows outer loop IndexVar 'i' (§5.8 ⑥ nested scope)");
    }

    // ── ⑦⑧ 参数遮蔽防护（StepScope.Resolve: LoopIndexVar > CaseParams > SuiteParams > Variables）──

    /// <summary>构造带 suite/case 参数的单 case suite（⑦⑧ 规则用）。</summary>
    private static TestSuite BuildSuite(
        IReadOnlyDictionary<string, ParameterValue>? suiteParams,
        IReadOnlyDictionary<string, ParameterValue>? caseParams,
        params TestCaseStep[] steps)
    {
        var testCase = new TestCase("c1", "case1", "", null, steps, null, Array.Empty<string>(),
            Parameters: caseParams);
        return new TestSuite("s1", new[] { testCase }, Array.Empty<string>(), Array.Empty<string>(),
            new TestSuiteConfig(), Parameters: suiteParams);
    }

    private static Dictionary<string, ParameterValue> NumberParams(params (string Name, double Value)[] kvps)
        => kvps.ToDictionary(k => k.Name, k => new ParameterValue(ParameterKind.Number, k.Value));

    [Fact]
    public void Assign_SameNameAsSuiteParam_ReportsCritical_8()
    {
        // Arrange: suite 参数 turnMs + Assign("turnMs") — Resolve 参数层先于 Variables，写入永远读不回
        var suite = BuildSuite(
            NumberParams(("turnMs", 200)), null,
            TestCaseStep.Create(new AssignStep("turnMs", "500")));

        var issues = _registry.Validate(suite);

        issues.Should().Contain(
            i => i.RuleId == "⑧" && i.Severity == ValidationSeverity.Critical,
            "AssignStep.Assign 'turnMs' shadows suite parameter 'turnMs' (write never read back)");
    }

    [Fact]
    public void ReadDidOutputVar_SameNameAsCaseParam_ReportsCritical_8()
    {
        // Arrange: case 参数 vin + ReadDid(OutputVar="vin") — 读回的始终是参数静态值
        var suite = BuildSuite(
            null, NumberParams(("vin", 1)),
            TestCaseStep.Create(new ReadDidStep(0xF190, "vin")));

        var issues = _registry.Validate(suite);

        issues.Should().Contain(
            i => i.RuleId == "⑧" && i.Severity == ValidationSeverity.Critical,
            "ReadDidStep.OutputVar 'vin' shadows case parameter 'vin'");
    }

    [Fact]
    public void RoutineControlOutputVar_SameNameAsSuiteParam_ReportsCritical_8()
    {
        // Arrange: suite 参数 rout + RoutineControl(OutputVar="rout")
        var suite = BuildSuite(
            NumberParams(("rout", 0)), null,
            TestCaseStep.Create(new RoutineControlStep(1, 0x0200, null, "rout")));

        var issues = _registry.Validate(suite);

        issues.Should().Contain(
            i => i.RuleId == "⑧" && i.Severity == ValidationSeverity.Critical,
            "RoutineControlStep.OutputVar 'rout' shadows suite parameter 'rout'");
    }

    [Fact]
    public void WriterInsideIfBody_SameNameAsParam_ReportsCritical_8()
    {
        // Arrange: If body 内 ReadDid(OutputVar="x") 与 suite 参数 x 同名 — body 遍历同样检查
        var body = new[] { TestCaseStep.Create(new ReadDidStep(0xF187, "x")) };
        var suite = BuildSuite(
            NumberParams(("x", 1)), null,
            TestCaseStep.Create(new IfStep("true", body, null)));

        var issues = _registry.Validate(suite);

        issues.Should().Contain(
            i => i.RuleId == "⑧" && i.Severity == ValidationSeverity.Critical,
            "ReadDidStep.OutputVar 'x' inside If body shadows suite parameter 'x'");
    }

    [Fact]
    public void NoNameCollision_NoRule7Or8()
    {
        // Arrange: 参数名与 writer 名全部不同 → 无 ⑦⑧（⑧ 阴性：不误报）
        var suite = BuildSuite(
            NumberParams(("turnMs", 200)), NumberParams(("vin", 1)),
            TestCaseStep.Create(new AssignStep("threshold", "5")),
            TestCaseStep.Create(new ReadDidStep(0xF190, "ecuVin")));

        var issues = _registry.Validate(suite);

        issues.Should().NotContain(i => i.RuleId == "⑦" || i.RuleId == "⑧",
            "no writer shares a name with a suite/case parameter and no case param duplicates a suite param");
    }

    [Fact]
    public void CaseParam_SameNameAsSuiteParam_ReportsMedium_7()
    {
        // Arrange: suite 与 case 都定义 turnMs — case 值生效，suite 值对该 case 不可见（可能非有意）
        var suite = BuildSuite(
            NumberParams(("turnMs", 200)), NumberParams(("turnMs", 500)),
            TestCaseStep.Create(new CommentStep("noop")));

        var issues = _registry.Validate(suite);

        issues.Should().Contain(
            i => i.RuleId == "⑦" && i.Severity == ValidationSeverity.Medium,
            "case parameter 'turnMs' shadows suite parameter 'turnMs'");
        issues.Should().ContainSingle(i => i.RuleId == "⑦",
            "one shadowed name = one ⑦ issue");
    }

    [Fact]
    public void DistinctParamNames_NoRule7()
    {
        // Arrange: suite {a} + case {b} — 无交集，无 ⑦（阴性）
        var suite = BuildSuite(
            NumberParams(("a", 1)), NumberParams(("b", 2)),
            TestCaseStep.Create(new CommentStep("noop")));

        var issues = _registry.Validate(suite);

        issues.Should().NotContain(i => i.RuleId == "⑦",
            "no case parameter name collides with a suite parameter name");
    }
}
