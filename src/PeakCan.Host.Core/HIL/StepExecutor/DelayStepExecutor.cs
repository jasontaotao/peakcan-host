namespace PeakCan.Host.Core.HIL.StepExecutor;

/// <summary>
/// Executes Delay steps. Returns Passed after the specified delay.
/// </summary>
internal sealed class DelayStepExecutor : IStepExecutor
{
    public TestCaseStepKind Kind => TestCaseStepKind.Delay;

    public async Task<StepResult> ExecuteAsync(TestCaseStep step, Contracts.IAssertionContext ctx, CancellationToken ct)
    {
        var p = (DelayStep)step.Parameters;
        await Task.Delay(p.Milliseconds, ct);

        return new StepResult(0, step.Kind, step.Label, StepStatus.Passed,
            $"Delayed {p.Milliseconds}ms", null, null, 0);
    }
}
