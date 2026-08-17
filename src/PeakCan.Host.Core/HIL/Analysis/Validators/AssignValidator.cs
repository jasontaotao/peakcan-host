using PeakCan.HIL.Core.HIL.Expressions;

namespace PeakCan.HIL.Core.HIL.Analysis;

/// <summary>
/// AssignStep 校验器（§5.8）。局部直接检查：
/// - 语法：Expression 表达式解析失败 → Critical。
/// 注意：规则 ⑥（Assign 与 IndexVar 同名）需要作用域上下文，由 DataFlowScanner 承担。
/// </summary>
public sealed class AssignValidator : IStepValidator
{
    /// <inheritdoc />
    public TestCaseStepKind Kind => TestCaseStepKind.Assign;

    /// <inheritdoc />
    public IReadOnlyList<ValidationIssue> Validate(TestCaseStep step, in StepValidationContext context)
    {
        var issues = new List<ValidationIssue>();
        var assignParams = (AssignStep)step.Parameters;

        // 语法：Expression 解析失败 → Critical
        var parse = context.Evaluator.Parse(assignParams.Expression);
        if (parse.IsError)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Critical, "语法", "Expression parse error",
                $"Assign expression parse error: {parse.Error!.Message} (§5.8 syntax)",
                context.StepPath, context.StepLabel));
        }

        return issues;
    }
}
