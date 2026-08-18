using System.Globalization;
using PeakCan.HIL.Core.HIL.Assertions;
using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.HIL.Core.HIL.StepExecutor;

/// <summary>
/// Executes WaitForFrame steps. Returns Passed when frame matches, Failed on timeout.
/// B.5: TimeoutMs is now string (supports ${name} interpolation).
/// </summary>
internal sealed class ExpectFrameStepExecutor : IStepExecutor
{
    private readonly Assertions.AssertionPrimitives _primitives;

    public ExpectFrameStepExecutor(Assertions.AssertionPrimitives primitives) => _primitives = primitives;
    public TestCaseStepKind Kind => TestCaseStepKind.WaitForFrame;

    public async Task<StepResult> ExecuteAsync(TestCaseStep step, IAssertionContext ctx, CancellationToken ct)
    {
        var p = (ExpectFrameStep)step.Parameters;
        var timeoutMs = int.Parse(p.TimeoutMs, CultureInfo.InvariantCulture);
        var result = await _primitives.WaitForFrameAsync(p.Id, p.DataMask, timeoutMs, ct);

        return new StepResult(0, step.Kind, step.Label,
            result.Passed ? StepStatus.Passed : StepStatus.Failed,
            result.Message, result.ActualValue, result.ExpectedValue, 0);
    }
}
