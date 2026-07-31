namespace PeakCan.Host.Core.HIL.StepExecutor;

/// <summary>
/// Executes WaitForSignal steps. Returns Passed when signal matches, Failed on timeout.
/// </summary>
internal sealed class WaitForSignalStepExecutor : IStepExecutor
{
    private readonly Assertions.AssertionPrimitives _primitives;

    public WaitForSignalStepExecutor(Assertions.AssertionPrimitives primitives) => _primitives = primitives;
    public TestCaseStepKind Kind => TestCaseStepKind.WaitForSignal;

    public async Task<StepResult> ExecuteAsync(TestCaseStep step, Contracts.IAssertionContext ctx, CancellationToken ct)
    {
        var p = (WaitForSignalStep)step.Parameters;
        // BUG-001 fix: pass timeoutMs so the step doesn't hang forever
        var result = await _primitives.WaitForSignalAsync(p.SignalName, p.Expected, p.Tolerance, p.TimeoutMs, ct);

        return new StepResult(0, step.Kind, step.Label,
            result.Passed ? StepStatus.Passed : StepStatus.Failed,
            result.Message, result.ActualValue, result.ExpectedValue, 0);
    }
}
