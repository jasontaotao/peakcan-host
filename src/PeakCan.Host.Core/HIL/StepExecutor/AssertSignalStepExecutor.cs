namespace PeakCan.HIL.Core.HIL.StepExecutor;

/// <summary>
/// Executes AssertSignal steps. Returns Passed when signal is within tolerance, Failed otherwise.
/// </summary>
internal sealed class AssertSignalStepExecutor : IStepExecutor
{
    private readonly Assertions.AssertionPrimitives _primitives;

    public AssertSignalStepExecutor(Assertions.AssertionPrimitives primitives) => _primitives = primitives;

    public TestCaseStepKind Kind => TestCaseStepKind.AssertSignal;

    public Task<StepResult> ExecuteAsync(TestCaseStep step, Contracts.IAssertionContext ctx, CancellationToken ct)
    {
        var p = (AssertSignalStep)step.Parameters;
        var result = _primitives.AssertSignal(p.SignalName, p.Expected, p.Tolerance);

        return Task.FromResult(new StepResult(0, step.Kind, step.Label,
            result.Passed ? StepStatus.Passed : StepStatus.Failed,
            result.Message, result.ActualValue, result.ExpectedValue, 0));
    }
}
