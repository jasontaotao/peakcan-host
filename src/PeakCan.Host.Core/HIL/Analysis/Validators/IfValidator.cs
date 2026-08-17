using PeakCan.HIL.Core.HIL.Expressions;

namespace PeakCan.HIL.Core.HIL.Analysis;

/// <summary>
/// IfStep 校验器（§5.8）。局部直接检查：
/// - 语法：Condition 表达式解析失败 → Critical。
/// - ④：容器 ExpectedVerdict ≠ Any → Critical。
/// </summary>
public sealed class IfValidator : IStepValidator
{
    /// <inheritdoc />
    public TestCaseStepKind Kind => TestCaseStepKind.If;

    /// <inheritdoc />
    public IReadOnlyList<ValidationIssue> Validate(TestCaseStep step, in StepValidationContext context)
    {
        var issues = new List<ValidationIssue>();
        var ifParams = (IfStep)step.Parameters;

        // ④：容器 ExpectedVerdict ≠ Any → Critical（控制流容器自身不应设预期判定）
        if (step.ExpectedVerdict != ExpectedVerdict.Any)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Critical, "④", "Container ExpectedVerdict",
                $"If container has ExpectedVerdict={step.ExpectedVerdict} (must be Any, §5.8 ④)",
                context.StepPath, context.StepLabel));
        }

        // 语法：Condition 表达式解析失败 → Critical
        var parse = context.Evaluator.Parse(ifParams.Condition);
        if (parse.IsError)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Critical, "语法", "Expression parse error",
                $"If condition parse error: {parse.Error!.Message} (§5.8 syntax)",
                context.StepPath, context.StepLabel));
        }

        return issues;
    }
}
