using PeakCan.HIL.Core.HIL.Expressions;

namespace PeakCan.HIL.Core.HIL.Analysis;

/// <summary>
/// 控制流校验器注册表（§5.8 入口）。聚合 4 per-kind validator + DataFlowScanner。
/// Critical 拦运行不拦保存；High/Medium 为警告可运行。
/// </summary>
public sealed class StepValidatorRegistry
{
    private readonly DataFlowScanner _scanner;

    public StepValidatorRegistry(ExpressionEvaluator evaluator, IDbcSignalLookup? dbcLookup = null)
    {
        var validators = new Dictionary<TestCaseStepKind, IStepValidator>
        {
            [TestCaseStepKind.If] = new IfValidator(),
            [TestCaseStepKind.Repeat] = new RepeatValidator(),
            [TestCaseStepKind.Loop] = new LoopValidator(),
            [TestCaseStepKind.Assign] = new AssignValidator(),
        };
        _scanner = new DataFlowScanner(evaluator, dbcLookup, validators);
    }

    /// <summary>校验整个 TestSuite，返回所有问题（含 Critical/High/Medium）。</summary>
    public IReadOnlyList<ValidationIssue> Validate(TestSuite suite)
    {
        var issues = new List<ValidationIssue>();
        foreach (var testCase in suite.Cases)
            issues.AddRange(_scanner.ScanCase(testCase));
        return issues;
    }

    /// <summary>是否存在 Critical 问题（用于拦运行/拦 AI 插入）。</summary>
    public bool HasCritical(TestSuite suite)
        => Validate(suite).Any(i => i.Severity == ValidationSeverity.Critical);
}
