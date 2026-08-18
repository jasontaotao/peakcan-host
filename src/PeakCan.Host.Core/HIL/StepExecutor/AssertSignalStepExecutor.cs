using System.Globalization;

namespace PeakCan.HIL.Core.HIL.StepExecutor;

/// <summary>
/// Executes AssertSignal steps. Returns Passed when signal is within tolerance, Failed otherwise.
/// B.5: Expected/Tolerance are now string (supports ${name} interpolation).
/// </summary>
internal sealed class AssertSignalStepExecutor : IStepExecutor
{
    private readonly Assertions.AssertionPrimitives _primitives;

    public AssertSignalStepExecutor(Assertions.AssertionPrimitives primitives) => _primitives = primitives;

    public TestCaseStepKind Kind => TestCaseStepKind.AssertSignal;

    public Task<StepResult> ExecuteAsync(TestCaseStep step, Contracts.IAssertionContext ctx, CancellationToken ct)
    {
        var p = (AssertSignalStep)step.Parameters;
        var expected = double.Parse(p.Expected, CultureInfo.InvariantCulture);
        var tolerance = double.Parse(p.Tolerance, CultureInfo.InvariantCulture);
        var result = _primitives.AssertSignal(p.SignalName, expected, tolerance);

        return Task.FromResult(new StepResult(0, step.Kind, step.Label,
            result.Passed ? StepStatus.Passed : StepStatus.Failed,
            result.Message, result.ActualValue, result.ExpectedValue, 0));
    }
}
