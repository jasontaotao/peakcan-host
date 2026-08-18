using System.Globalization;

namespace PeakCan.HIL.Core.HIL.StepExecutor;

/// <summary>
/// Executes Delay steps. Returns Passed after the specified delay.
/// B.5: Milliseconds is now string (supports ${name} interpolation, resolved by engine).
/// </summary>
internal sealed class DelayStepExecutor : IStepExecutor
{
    public TestCaseStepKind Kind => TestCaseStepKind.Delay;

    public async Task<StepResult> ExecuteAsync(TestCaseStep step, Contracts.IAssertionContext ctx, CancellationToken ct)
    {
        var p = (DelayStep)step.Parameters;
        var ms = int.Parse(p.Milliseconds, CultureInfo.InvariantCulture);
        await Task.Delay(ms, ct);

        return new StepResult(0, step.Kind, step.Label, StepStatus.Passed,
            $"Delayed {ms}ms", null, null, 0);
    }
}
