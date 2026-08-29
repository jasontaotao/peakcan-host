using System.Text;
using PeakCan.HIL.Core.HIL;

namespace PeakCan.Host.Infrastructure.HIL.Analysis;

/// <summary>
/// Sprint 14: Constructs LLM-friendly prompt from failed test cases.
/// Returns plain string (not ChatMessage[]).
/// </summary>
public static class HilPromptBuilder
{
    private const int MaxFramesInPrompt = 20;

    public static string Build(TestSuiteResult result, EcuScript? ecuScript = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Failed Test Cases");

        foreach (var c in result.CaseResults.Where(c => !c.Passed))
        {
            sb.AppendLine($"- Case: {c.TestCaseName}");
            sb.AppendLine($"  Reason: {c.FailureReason ?? "unknown"}");

            foreach (var s in c.StepResults.Where(s => s.Status == StepStatus.Failed))
            {
                sb.AppendLine($"  Step {s.StepIndex} ({s.Kind}): {s.Message}");
                // G5: 通道归属（多通道失败分析不被误导根因；仅非空时渲染）
                if (s.Channel is not null)
                    sb.AppendLine($"    Channel: {s.Channel}");
                if (s.ActualValue is not null)
                    sb.AppendLine($"    Actual: {s.ActualValue}, Expected: {s.ExpectedValue}");

                if (s.FramesAroundFailure is { Count: > 0 })
                {
                    sb.AppendLine("    Frames:");
                    foreach (var f in s.FramesAroundFailure.Take(MaxFramesInPrompt))
                    {
                        var idStr = f.Id.IsExtended ? $"0x{f.Id.Raw:X8}" : $"0x{f.Id.Raw:X3}";
                        var dataHex = BitConverter.ToString(f.Data.Span.ToArray()).Replace("-", " ");
                        sb.AppendLine($"      {idStr} [{dataHex}] @ {f.Timestamp.TotalMicroseconds}µs");
                    }
                }
            }
        }

        if (ecuScript is not null)
        {
            sb.AppendLine("## ECU Configuration");
            sb.AppendLine($"ECU: {ecuScript.Name}");
            // Note: State machine states are inferred from transitions during analysis.
            // The LLM can deduce state names from the failure context above.
        }

        return sb.ToString();
    }
}
