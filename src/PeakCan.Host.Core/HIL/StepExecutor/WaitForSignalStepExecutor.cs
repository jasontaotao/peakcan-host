using System.Globalization;

namespace PeakCan.HIL.Core.HIL.StepExecutor;

/// <summary>
/// Executes WaitForSignal steps. Returns Passed when signal matches, Failed on timeout.
/// B.5: Expected/Tolerance/TimeoutMs are now string (supports ${name} interpolation).
/// </summary>
internal sealed class WaitForSignalStepExecutor : IStepExecutor
{
    private readonly Assertions.AssertionPrimitives _primitives;

    public WaitForSignalStepExecutor(Assertions.AssertionPrimitives primitives) => _primitives = primitives;
    public TestCaseStepKind Kind => TestCaseStepKind.WaitForSignal;

    public async Task<StepResult> ExecuteAsync(TestCaseStep step, Contracts.IAssertionContext ctx, CancellationToken ct)
    {
        var p = (WaitForSignalStep)step.Parameters;
        var expected = double.Parse(p.Expected, CultureInfo.InvariantCulture);
        var tolerance = double.Parse(p.Tolerance, CultureInfo.InvariantCulture);
        var timeoutMs = int.Parse(p.TimeoutMs, CultureInfo.InvariantCulture);
        var result = await _primitives.WaitForSignalAsync(p.SignalName, expected, tolerance, timeoutMs, ct);

        return new StepResult(0, step.Kind, step.Label,
            result.Passed ? StepStatus.Passed : StepStatus.Failed,
            result.Message, result.ActualValue, result.ExpectedValue, 0);
    }
}
