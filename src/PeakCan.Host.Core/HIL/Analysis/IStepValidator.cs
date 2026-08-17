using PeakCan.HIL.Core.HIL.Expressions;

namespace PeakCan.HIL.Core.HIL.Analysis;

/// <summary>
/// 单步骤校验上下文：承载表达式解析器 + 步骤定位信息。
/// per-kind validator 用此做局部直接检查（语法/④/⑤a/⑤b）。
/// 需要整树状态/作用域的规则（①-⑥ 树遍历）由 <see cref="DataFlowScanner"/> 承担。
/// </summary>
public readonly record struct StepValidationContext(
    ExpressionEvaluator Evaluator,
    string StepPath,
    int Depth,
    string? StepLabel);

/// <summary>
/// 单步骤校验器接口（per-kind）。负责当前步骤的局部直接检查规则。
/// 实现覆盖：IfValidator / RepeatValidator / LoopValidator / AssignValidator。
/// </summary>
public interface IStepValidator
{
    /// <summary>该 validator 负责的步骤种类。</summary>
    TestCaseStepKind Kind { get; }

    /// <summary>校验单个步骤，返回问题列表（可能为空）。</summary>
    /// <param name="step">待校验步骤（TestCaseStep 包装，含 Parameters/Label/ExpectedVerdict）。</param>
    /// <param name="context">校验上下文（解析器 + 定位）。</param>
    IReadOnlyList<ValidationIssue> Validate(TestCaseStep step, in StepValidationContext context);
}
