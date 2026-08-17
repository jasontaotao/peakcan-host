namespace PeakCan.HIL.Core.HIL.Analysis;

/// <summary>
/// 静态校验严重度（§5.8 分级）。
/// </summary>
public enum ValidationSeverity
{
    /// <summary>Critical：必然非法/必然悬空，拦运行（允许保存 WIP）。</summary>
    Critical,

    /// <summary>High：可能悬空，警告可运行。</summary>
    High,

    /// <summary>Medium：可读性建议（如嵌套过深），不拦运行。</summary>
    Medium,
}

/// <summary>
/// 静态校验问题记录（§5.8）。承载规则号/名称/消息/定位。
/// 不可变 record，便于聚合与去重。
/// </summary>
/// <param name="Severity">严重度。</param>
/// <param name="RuleId">规则号（"语法"/"④"/"⑤a"/"①" 等）。</param>
/// <param name="RuleName">人类可读规则名。</param>
/// <param name="Message">问题描述。</param>
/// <param name="StepPath">步骤路径（如 "0.1"），顶层为 "0"；null 表示 case 级。</param>
/// <param name="StepLabel">步骤 Label（可空）。</param>
public sealed record ValidationIssue(
    ValidationSeverity Severity,
    string RuleId,
    string RuleName,
    string Message,
    string? StepPath,
    string? StepLabel);
