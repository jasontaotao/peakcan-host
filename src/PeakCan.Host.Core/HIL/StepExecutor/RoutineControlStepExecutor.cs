using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.Uds;

namespace PeakCan.HIL.Core.HIL.StepExecutor;

/// <summary>
/// Executes RoutineControl steps. Starts/stops/queries a routine via UDS;
/// result bytes stored into <see cref="IStepVariableStore"/>.
/// </summary>
internal sealed class RoutineControlStepExecutor : IStepExecutor
{
    private readonly UdsClient _uds;

    public RoutineControlStepExecutor(UdsClient uds) => _uds = uds;
    public TestCaseStepKind Kind => TestCaseStepKind.RoutineControl;

    public async Task<StepResult> ExecuteAsync(TestCaseStep step, IAssertionContext ctx, CancellationToken ct)
    {
        var p = (RoutineControlStep)step.Parameters;
        try
        {
            var result = await _uds.RoutineControlAsync(p.ControlType, p.RoutineId, p.Data, ct);
            if (p.OutputVar is { } varName && ctx is IStepVariableStore store)
                store.Variables[varName] = result;
            return new StepResult(0, step.Kind, step.Label, StepStatus.Passed,
                result.Length > 0
                    ? $"Routine 0x{p.RoutineId:X4} type {p.ControlType}: {Convert.ToHexString(result)}"
                    : $"Routine 0x{p.RoutineId:X4} type {p.ControlType}: OK", null, null, 0);
        }
        catch (UdsException ex)
        {
            return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"RoutineControl 0x{p.RoutineId:X4} failed: {ex.Message}", null, null, 0);
        }
    }
}
