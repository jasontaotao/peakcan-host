using PeakCan.HIL.Core.HIL.Expressions;

namespace PeakCan.HIL.Core.HIL.Analysis;

/// <summary>
/// RepeatStep 校验器（§5.8）。局部直接检查：
/// - 语法：While 模式 Condition / Fixed 模式 Count 解析失败 → Critical。
/// - ④：容器 ExpectedVerdict ≠ Any → Critical。
/// - ⑤b：MaxIterations 越界（&lt;1 或 &gt;100000）→ Critical。
/// </summary>
public sealed class RepeatValidator : IStepValidator
{
    /// <summary>MaxIterations 上限（§5.8 ⑤b）。</summary>
    private const int MaxIterationsUpperBound = 100_000;

    /// <inheritdoc />
    public TestCaseStepKind Kind => TestCaseStepKind.Repeat;

    /// <inheritdoc />
    public IReadOnlyList<ValidationIssue> Validate(TestCaseStep step, in StepValidationContext context)
    {
        var issues = new List<ValidationIssue>();
        var rp = (RepeatStep)step.Parameters;

        // ④：容器 ExpectedVerdict ≠ Any → Critical
        if (step.ExpectedVerdict != ExpectedVerdict.Any)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Critical, "④", "Container ExpectedVerdict",
                $"Repeat container has ExpectedVerdict={step.ExpectedVerdict} (must be Any, §5.8 ④)",
                context.StepPath, context.StepLabel));
        }

        // ⑤b：MaxIterations 越界 → Critical（B.5: MaxIterations 改为 string，需先 parse）
        if (int.TryParse(rp.MaxIterations, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var maxIterVal)
            && (maxIterVal < 1 || maxIterVal > MaxIterationsUpperBound))
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Critical, "⑤b", "MaxIterations out of bounds",
                $"Repeat MaxIterations={rp.MaxIterations} (must be in [1, 100000], §5.8 ⑤b)",
                context.StepPath, context.StepLabel));
        }

        // 语法：Condition（While 模式）/ Count（Fixed 模式）解析失败 → Critical
        if (rp.Mode == RepeatMode.While)
        {
            var condParse = context.Evaluator.Parse(rp.Condition ?? "false");
            if (condParse.IsError)
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Critical, "语法", "Expression parse error",
                    $"Repeat while condition parse error: {condParse.Error!.Message} (§5.8 syntax)",
                    context.StepPath, context.StepLabel));
            }
        }
        else // Fixed
        {
            var countParse = context.Evaluator.Parse(rp.Count ?? "1");
            if (countParse.IsError)
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Critical, "语法", "Expression parse error",
                    $"Repeat fixed count parse error: {countParse.Error!.Message} (§5.8 syntax)",
                    context.StepPath, context.StepLabel));
            }
        }

        return issues;
    }
}
