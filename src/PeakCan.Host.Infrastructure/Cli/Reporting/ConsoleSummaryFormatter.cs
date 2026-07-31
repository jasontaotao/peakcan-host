using System.Text;
using PeakCan.Host.Core.HIL;

namespace PeakCan.Host.Infrastructure.Cli.Reporting;

/// <summary>
/// Formats a <see cref="TestSuiteResult"/> as plain-text summary with pass/fail symbols.
/// No ANSI escape codes — uses Unicode symbols (✔ ✘ ○ ▸) for broad terminal compatibility.
/// </summary>
public static class ConsoleSummaryFormatter
{
    private const string PassSymbol = "✔";
    private const string FailSymbol = "✘";
    private const string SkipSymbol = "○";
    private const string CommentSymbol = "▸";

    /// <summary>
    /// Format the test suite result as a plain-text multi-line summary.
    /// </summary>
    public static string Format(TestSuiteResult result)
    {
        var sb = new StringBuilder();

        // Header
        sb.AppendLine($"Suite: {result.SuiteName}");
        sb.AppendLine(new string('─', 40));

        // Summary line
        var rate = result.PassRate * 100.0;
        sb.AppendLine($"Total: {result.TotalCases}  " +
                      $"Passed: {result.PassedCases}  " +
                      $"Failed: {result.FailedCases}  " +
                      $"Skipped: {result.SkippedCases}  " +
                      $"Elapsed: {result.ElapsedMs}ms  " +
                      $"Rate: {rate:F1}%");

        sb.AppendLine();

        // Per-case status
        foreach (var c in result.CaseResults)
        {
            var symbol = c.Passed ? PassSymbol : FailSymbol;
            var stepInfo = $"({c.PassedSteps}/{c.TotalSteps} steps)";
            sb.AppendLine($"  {symbol} {c.TestCaseName} {stepInfo} {c.ElapsedMs}ms");

            // List failed steps with actual vs expected
            foreach (var step in c.StepResults)
            {
                if (step.Status != StepStatus.Failed) continue;

                sb.AppendLine($"      ✘ Step {step.StepIndex} [{step.Kind}]: {step.Message}");
                if (step.ActualValue is not null || step.ExpectedValue is not null)
                {
                    sb.AppendLine($"          Expected: {step.ExpectedValue ?? "(null)"}");
                    sb.AppendLine($"          Actual:   {step.ActualValue ?? "(null)"}");
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine(result.AllPassed
            ? $"{PassSymbol} ALL PASSED"
            : $"{FailSymbol} {result.FailedCases} FAILED");

        return sb.ToString();
    }
}
