using System.Globalization;
using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.HIL.Core.HIL.StepExecutor;

/// <summary>
/// Executes AssertDidValue steps. Reads a value previously written into
/// <see cref="IStepVariableStore"/> and asserts it. The channel consumer that
/// closes the step-to-step data-passing loop. Does not call UDS.
/// B.5: TimeoutMs is now string (supports ${name} interpolation).
/// </summary>
internal sealed class AssertDidValueStepExecutor : IStepExecutor
{
    public TestCaseStepKind Kind => TestCaseStepKind.AssertDidValue;

    public async Task<StepResult> ExecuteAsync(TestCaseStep step, IAssertionContext ctx, CancellationToken ct)
    {
        var p = (AssertDidValueStep)step.Parameters;
        var timeoutMs = int.Parse(p.TimeoutMs, CultureInfo.InvariantCulture);
        if (ctx is not IStepVariableStore store)
            return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                "Assertion context does not support IStepVariableStore", null, null, 0);

        // 轮询等键出现（ReadDid 若因 FailurePolicy 被跳过，本步骤超时失败并给出明确原因）
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!ct.IsCancellationRequested && !store.Variables.ContainsKey(p.VarKey) && Environment.TickCount64 < deadline)
            await Task.Delay(50, ct);

        if (!store.Variables.TryGetValue(p.VarKey, out var raw) || raw is not byte[] actual)
            return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"Variable '{p.VarKey}' not available within {timeoutMs}ms", null, null, 0);

        bool pass = p.Expected is null || actual.AsSpan().SequenceEqual(p.Expected);
        return new StepResult(0, step.Kind, step.Label, pass ? StepStatus.Passed : StepStatus.Failed,
            pass ? $"Variable '{p.VarKey}' OK ({Convert.ToHexString(actual)})"
                 : $"Variable '{p.VarKey}' = {Convert.ToHexString(actual)}, expected {Convert.ToHexString(p.Expected!)}",
            null, null, 0);
    }
}
