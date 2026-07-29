namespace PeakCan.Host.Core.HIL.StepExecutor;

/// <summary>
/// Executes AssertRange steps. Returns Passed when signal is within [min, max], Failed otherwise.
/// </summary>
internal sealed class AssertRangeStepExecutor : IStepExecutor
{
    private readonly Assertions.AssertionPrimitives _primitives;

    public AssertRangeStepExecutor(Assertions.AssertionPrimitives primitives) => _primitives = primitives;

    public TestCaseStepKind Kind => TestCaseStepKind.AssertRange;

    public Task<StepResult> ExecuteAsync(TestCaseStep step, Contracts.IAssertionContext ctx, CancellationToken ct)
    {
        var p = (AssertRangeStep)step.Parameters;
        var result = _primitives.AssertRange(p.SignalName, p.Min, p.Max);

        return Task.FromResult(new StepResult(0, step.Kind, step.Label,
            result.Passed ? StepStatus.Passed : StepStatus.Failed,
            result.Message, result.ActualValue, result.ExpectedValue, 0));
    }
}
