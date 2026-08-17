using PeakCan.HIL.Core.HIL.Expressions;

namespace PeakCan.HIL.Core.HIL.Analysis;

/// <summary>
/// LoopStep 校验器（§5.8）。局部直接检查：
/// - 语法：From/To/Step 表达式解析失败 → Critical。
/// - ④：容器 ExpectedVerdict ≠ Any → Critical。
/// - ⑤a：Step 为常量且 ≤0 → Critical（非常量/含变量时不报，运行期兜底）。
/// </summary>
public sealed class LoopValidator : IStepValidator
{
    /// <inheritdoc />
    public TestCaseStepKind Kind => TestCaseStepKind.Loop;

    /// <inheritdoc />
    public IReadOnlyList<ValidationIssue> Validate(TestCaseStep step, in StepValidationContext context)
    {
        var issues = new List<ValidationIssue>();
        var lp = (LoopStep)step.Parameters;

        // ④：容器 ExpectedVerdict ≠ Any → Critical
        if (step.ExpectedVerdict != ExpectedVerdict.Any)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Critical, "④", "Container ExpectedVerdict",
                $"Loop container has ExpectedVerdict={step.ExpectedVerdict} (must be Any, §5.8 ④)",
                context.StepPath, context.StepLabel));
        }

        // 语法：From/To/Step 解析失败 → Critical
        foreach (var (expr, field) in new[] { (lp.From, "from"), (lp.To, "to"), (lp.Step, "step") })
        {
            var parse = context.Evaluator.Parse(expr);
            if (parse.IsError)
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Critical, "语法", "Expression parse error",
                    $"Loop {field} parse error: {parse.Error!.Message} (§5.8 syntax)",
                    context.StepPath, context.StepLabel));
            }
        }

        // ⑤a：Step 为常量且 ≤0 → Critical（仅静态可判定的常量字面量；含 hex 字面量）
        var stepParse = context.Evaluator.Parse(lp.Step);
        if (!stepParse.IsError)
        {
            double? stepConst = stepParse.Ast switch
            {
                NumberLiteral n => n.Value,
                HexLiteral h => h.Value,
                _ => null, // 非常量（含变量）→ 静态无法判定，运行期兜底
            };
            if (stepConst is <= 0)
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Critical, "⑤a", "Loop Step <= 0",
                    $"Loop Step={lp.Step} (must be > 0, §5.8 ⑤a)",
                    context.StepPath, context.StepLabel));
            }
        }

        return issues;
    }
}
