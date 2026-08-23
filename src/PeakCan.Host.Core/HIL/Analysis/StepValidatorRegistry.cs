using PeakCan.HIL.Core.HIL;
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
        issues.AddRange(ValidateTargetChannels(suite));
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

    /// <summary>
    /// TargetChannel 校验（Q3）。
    /// (a) suite 未声明 Channels + 任一 MVP 步骤带非空 TargetChannel → Critical
    ///     （TargetChannel 必须引用 suite.Channels 声明的通道名）。
    /// (b) 步骤 TargetChannel 引用了未声明的通道名 → Critical。
    /// 单通道 suite（无 Channels、无 TargetChannel）零变化。
    /// </summary>
    private IEnumerable<ValidationIssue> ValidateTargetChannels(TestSuite suite)
    {
        // 声明的通道名集合（null/空 = 单通道，无声明）
        var declared = suite.Channels is { Count: > 0 } chs
            ? new HashSet<string>(chs.Select(c => c.Name), StringComparer.Ordinal)
            : null;

        foreach (var testCase in suite.Cases)
        {
            foreach (var step in testCase.Steps)
            {
                var target = TryGetTargetChannel(step.Parameters);
                if (string.IsNullOrEmpty(target))
                    continue; // 无 TargetChannel = 默认通道，合法

                // (a) suite 未声明 Channels 却用 TargetChannel → Critical
                if (declared is null)
                {
                    yield return new ValidationIssue(
                        ValidationSeverity.Critical, "MC-1", "TargetChannel without suite.Channels",
                        $"case '{testCase.Id}' step has TargetChannel='{target}' but the suite declares no Channels. " +
                        $"Declare Channels at suite level before referencing a channel by name.",
                        testCase.Id, testCase.Name);
                    continue;
                }

                // (b) 引用未声明的通道名 → Critical
                if (!declared.Contains(target))
                {
                    yield return new ValidationIssue(
                        ValidationSeverity.Critical, "MC-2", "TargetChannel not declared",
                        $"case '{testCase.Id}' step TargetChannel='{target}' is not declared in suite.Channels. " +
                        $"Declared channels: {string.Join(", ", declared)}.",
                        testCase.Id, testCase.Name);
                }
            }
        }
    }

    /// <summary>
    /// 从 StepParameters 提取 TargetChannel（仅 5 个 MVP 帧步骤类型有此字段；
    /// 其余类型返回 null）。pattern match 避免 IAssertionContext cast。
    /// </summary>
    private static string? TryGetTargetChannel(StepParameters p) => p switch
    {
        SendFrameStep s => s.TargetChannel,
        ExpectFrameStep s => s.TargetChannel,
        AssertNoFrameStep s => s.TargetChannel,
        AssertFrameCountStep s => s.TargetChannel,
        AssertCycleTimeStep s => s.TargetChannel,
        _ => null,
    };

    /// <summary>是否存在 Critical 问题（用于拦运行/拦 AI 插入）。</summary>
    public bool HasCritical(TestSuite suite)
        => Validate(suite).Any(i => i.Severity == ValidationSeverity.Critical);
}
