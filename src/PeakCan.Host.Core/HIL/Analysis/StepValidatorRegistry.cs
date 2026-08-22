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

    /// <summary>
    /// 校验整个 TestSuite，返回所有问题（含 Critical/High/Medium）。
    /// ⑦：case 参数与 suite 参数同名 → Medium（StepScope.Resolve case 层先于 suite 层，
    /// case 值生效、suite 值被遮蔽——可能非有意，警告不拦运行）。
    /// ⑧（writer 遮蔽参数 → Critical）由 DataFlowScanner 按步骤树报。
    /// </summary>
    public IReadOnlyList<ValidationIssue> Validate(TestSuite suite)
    {
        var issues = new List<ValidationIssue>();
        var suiteParamNames = suite.Parameters is { Count: > 0 } sp ? sp.Keys.ToList() : null;
        for (int c = 0; c < suite.Cases.Count; c++)
        {
            var testCase = suite.Cases[c];

            // ⑦ case 参数遮蔽 suite 参数（每个重名一条，case 级定位）
            if (suiteParamNames is not null && testCase.Parameters is { Count: > 0 } cps)
            {
                foreach (var name in cps.Keys)
                {
                    if (!suiteParamNames.Contains(name)) continue;
                    issues.Add(new ValidationIssue(
                        ValidationSeverity.Medium, "⑦", "Case param shadows suite param",
                        $"case '{testCase.Id}' parameter '{name}' shadows the suite parameter of the same name " +
                        $"(StepScope.Resolve checks CaseParams before SuiteParams; the suite value is invisible to this case)",
                        testCase.Id, testCase.Name));
                }
            }

            // ⑧ 检查集 = case 参数名 ∪ suite 参数名（两层参数都遮蔽 Variables 层）
            IReadOnlyCollection<string>? paramNames = null;
            if (suiteParamNames is not null || testCase.Parameters is { Count: > 0 })
            {
                var names = new List<string>(suiteParamNames ?? (IReadOnlyCollection<string>)Array.Empty<string>());
                if (testCase.Parameters is { Count: > 0 } cp)
                    names.AddRange(cp.Keys.Where(k => !names.Contains(k)));
                paramNames = names;
            }
            issues.AddRange(_scanner.ScanCase(testCase, paramNames));
        }
        return issues;
    }

    /// <summary>是否存在 Critical 问题（用于拦运行/拦 AI 插入）。</summary>
    public bool HasCritical(TestSuite suite)
        => Validate(suite).Any(i => i.Severity == ValidationSeverity.Critical);
}
