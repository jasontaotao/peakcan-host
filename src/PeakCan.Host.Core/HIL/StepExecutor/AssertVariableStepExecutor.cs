using System.Globalization;
using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.HIL.Core.HIL.StepExecutor;

internal sealed class AssertVariableStepExecutor : IStepExecutor
{
    public TestCaseStepKind Kind => TestCaseStepKind.AssertVariable;

    public async Task<StepResult> ExecuteAsync(TestCaseStep step, IAssertionContext ctx, CancellationToken ct)
    {
        var p = (AssertVariableStep)step.Parameters;
        if (ctx is not IStepVariableStore store)
            return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                "Assertion context does not support IStepVariableStore", null, null, 0);

        // B.5: TimeoutMs is now string; parse to int
        var timeoutMs = int.Parse(p.TimeoutMs, CultureInfo.InvariantCulture);
        // 轮询等键出现（前置步骤若因 FailurePolicy 被跳过, 变量永不出现 →
        // 超时失败并给出明确原因）。跟随 AssertDidValueStepExecutor 模式。
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!ct.IsCancellationRequested
               && !store.Variables.ContainsKey(p.VarKey)
               && Environment.TickCount64 < deadline)
            await Task.Delay(50, ct);

        if (!store.Variables.TryGetValue(p.VarKey, out var actual))
            return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"Variable '{p.VarKey}' not available within {timeoutMs}ms", null, null, 0);

        bool pass;
        string? actualStr, expectedStr;

        if (p.ExpectedHexBytes is { } expHex)
        {
            // hex 比较模式（byte[] 直接比较，跟随 AssertDidValueStepExecutor）
            expectedStr = Convert.ToHexString(expHex);
            if (actual is byte[] actualBytes)
            {
                actualStr = Convert.ToHexString(actualBytes);
                pass = actualBytes.AsSpan().SequenceEqual(expHex);
            }
            else
            {
                actualStr = actual?.ToString() ?? "(null)";
                pass = false;
            }
        }
        else if (p.ExpectedNumeric is { } expNumStr && double.TryParse(expNumStr, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var expNum))
        {
            // 数值比较模式（B.5: ExpectedNumeric is now string?）
            var tolerance = double.Parse(p.Tolerance, CultureInfo.InvariantCulture);
            expectedStr = expNum.ToString("G", CultureInfo.InvariantCulture);
            if (actual is double actualDbl)
            {
                actualStr = actualDbl.ToString("G", CultureInfo.InvariantCulture);
                pass = Math.Abs(actualDbl - expNum) <= tolerance;
            }
            else
            {
                actualStr = actual?.ToString() ?? "(null)";
                pass = false;
            }
        }
        else
        {
            return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"No expected value specified for '{p.VarKey}'", null, null, 0);
        }

        return new StepResult(0, step.Kind, step.Label,
            pass ? StepStatus.Passed : StepStatus.Failed,
            pass ? $"Variable '{p.VarKey}' matches" : $"Variable '{p.VarKey}' mismatch: expected {expectedStr}, actual {actualStr}",
            actualStr, expectedStr, 0);
    }
}
