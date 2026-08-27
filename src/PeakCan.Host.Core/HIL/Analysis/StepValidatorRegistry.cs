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
        issues.AddRange(ValidateUdsChannelConfigs(suite));
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
    /// TargetChannel 校验（Q3 + §2.4）。
    /// (a) suite 未声明 Channels + 任一步骤带非空 TargetChannel → Critical
    ///     （TargetChannel 必须引用 suite.Channels 声明的通道名）。
    /// (b) 步骤 TargetChannel 引用了未声明的通道名 → Critical。
    /// MC-3 (§2.4): UDS/DTC 步骤 TargetChannel 指向的通道无 UDS ID 配置 → High。
    /// 单通道 suite（无 Channels、无 TargetChannel）零变化。
    /// </summary>
    private IEnumerable<ValidationIssue> ValidateTargetChannels(TestSuite suite)
    {
        // 声明的通道名 → 配置映射（null = 单通道，无声明）
        var declared = suite.Channels is { Count: > 0 } chs
            ? chs.ToDictionary(c => c.Name, StringComparer.Ordinal)
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
                if (!declared.TryGetValue(target, out var channel))
                {
                    yield return new ValidationIssue(
                        ValidationSeverity.Critical, "MC-2", "TargetChannel not declared",
                        $"case '{testCase.Id}' step TargetChannel='{target}' is not declared in suite.Channels. " +
                        $"Declared channels: {string.Join(", ", declared.Keys)}.",
                        testCase.Id, testCase.Name);
                    continue;
                }

                // MC-3 (§2.4): UDS/DTC 步骤路由到的通道必须配齐 UDS ID（运行时 per-channel 栈
                // 要求两者非空，HeadlessHostBuilder 187 行；任一缺失 → resolver fallback 默认栈，
                // 诊断会静默读到默认通道/默认总线的 ECU——正是该规则要拦的静默错路）。
                if (IsUdsStep(step.Parameters) && (channel.UdsRequestId is null || channel.UdsResponseId is null))
                {
                    var missing = channel.UdsRequestId is null && channel.UdsResponseId is null
                        ? "no UdsRequestId/UdsResponseId"
                        : channel.UdsRequestId is null ? "missing UdsRequestId" : "missing UdsResponseId";
                    yield return new ValidationIssue(
                        ValidationSeverity.High, "MC-3", "UDS step targets channel without complete UDS IDs",
                        $"case '{testCase.Id}' UDS step TargetChannel='{target}' but channel '{target}' has {missing} " +
                        $"configured. The step would fall back to the default UDS stack (wrong bus/ECU).",
                        testCase.Id, testCase.Name);
                }
            }
        }
    }

    /// <summary>
    /// §2.4 UDS 通道配置校验（Task 10）：
    /// MC-4 同通道 UdsRequestId == UdsResponseId → High（请求/响应过滤 ID 不得相同）；
    /// MC-5 与其他通道 UDS ID 冲突 → Medium（物理隔离可能无害，但需确认总线拓扑）。
    /// </summary>
    private IEnumerable<ValidationIssue> ValidateUdsChannelConfigs(TestSuite suite)
    {
        if (suite.Channels is not { Count: > 0 } channels) yield break;

        foreach (var ch in channels)
        {
            if (ch.UdsRequestId is { } req && ch.UdsResponseId is { } resp && req == resp)
            {
                yield return new ValidationIssue(
                    ValidationSeverity.High, "MC-4", "UdsRequestId equals UdsResponseId",
                    $"channel '{ch.Name}' UdsRequestId == UdsResponseId == 0x{req:X}. " +
                    $"Request and response IDs must differ within a channel.",
                    null, ch.Name);
            }
        }

        for (int i = 0; i < channels.Count; i++)
        {
            for (int j = i + 1; j < channels.Count; j++)
            {
                foreach (var issue in FindUdsIdConflicts(channels[i], channels[j]))
                    yield return issue;
            }
        }
    }

    private static IEnumerable<ValidationIssue> FindUdsIdConflicts(ChannelConfig a, ChannelConfig b)
    {
        foreach (var (aid, aside) in UdsIds(a))
        {
            foreach (var (bid, bside) in UdsIds(b))
            {
                if (aid != bid) continue;
                yield return new ValidationIssue(
                    ValidationSeverity.Medium, "MC-5", "UDS ID conflicts across channels",
                    $"channel '{a.Name}' {aside} 0x{aid:X} conflicts with channel '{b.Name}' {bside} 0x{bid:X}. " +
                    $"Physical isolation may allow identical IDs, but verify the bus topology.",
                    null, $"{a.Name}/{b.Name}");
            }
        }
    }

    private static IEnumerable<(uint Id, string Side)> UdsIds(ChannelConfig ch)
    {
        if (ch.UdsRequestId is { } req) yield return (req, "Request");
        if (ch.UdsResponseId is { } resp) yield return (resp, "Response");
    }

    /// <summary>UDS/DTC 诊断类步骤（运行需要通道配 UdsRequestId/UdsResponseId）。</summary>
    private static bool IsUdsStep(StepParameters p) => p is
        ReadDidStep or WriteDidStep or SessionControlStep or ClearDtcStep or RoutineControlStep or
        SecurityAccessStep or AssertDtcStep or AssertNrcStep or ECUResetStep or
        CommunicationControlStep or IOControlStep;

    /// <summary>
    /// 从 StepParameters 提取 TargetChannel（全部带此字段的步骤类型：
    /// 5 个 MVP 帧步骤 + 11 个 UDS/DTC 步骤 + 2 个时间窗信号断言，Task B/C 扩展）。
    /// pattern match 避免 IAssertionContext cast。
    /// </summary>
    private static string? TryGetTargetChannel(StepParameters p) => p switch
    {
        SendFrameStep s => s.TargetChannel,
        ExpectFrameStep s => s.TargetChannel,
        AssertNoFrameStep s => s.TargetChannel,
        AssertFrameCountStep s => s.TargetChannel,
        AssertCycleTimeStep s => s.TargetChannel,
        ReadDidStep s => s.TargetChannel,
        WriteDidStep s => s.TargetChannel,
        SessionControlStep s => s.TargetChannel,
        ClearDtcStep s => s.TargetChannel,
        RoutineControlStep s => s.TargetChannel,
        SecurityAccessStep s => s.TargetChannel,
        AssertDtcStep s => s.TargetChannel,
        AssertNrcStep s => s.TargetChannel,
        ECUResetStep s => s.TargetChannel,
        CommunicationControlStep s => s.TargetChannel,
        IOControlStep s => s.TargetChannel,
        AssertSignalWithinStep s => s.TargetChannel,
        AssertStableStep s => s.TargetChannel,
        _ => null,
    };

    /// <summary>是否存在 Critical 问题（用于拦运行/拦 AI 插入）。</summary>
    public bool HasCritical(TestSuite suite)
        => Validate(suite).Any(i => i.Severity == ValidationSeverity.Critical);
}
