using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.Uds;

namespace PeakCan.HIL.Core.HIL.StepExecutor;

/// <summary>
/// Executes ClearDtc steps. Clears DTCs in a group via UDS.
/// </summary>
internal sealed class ClearDtcStepExecutor : IStepExecutor
{
    private readonly UdsClient _uds;

    public ClearDtcStepExecutor(UdsClient uds) => _uds = uds;
    public TestCaseStepKind Kind => TestCaseStepKind.ClearDtc;

    public async Task<StepResult> ExecuteAsync(TestCaseStep step, IAssertionContext ctx, CancellationToken ct)
    {
        var p = (ClearDtcStep)step.Parameters;
        try
        {
            await _uds.ClearDiagnosticInformationAsync(p.Group, ct);
            return new StepResult(0, step.Kind, step.Label, StepStatus.Passed,
                p.Group == 0xFFFFFF
                    ? "Cleared all DTCs"
                    : $"Cleared DTC group 0x{p.Group:X6}", null, null, 0);
        }
        catch (UdsException ex)
        {
            return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"ClearDtc failed: {ex.Message}", null, null, 0);
        }
    }
}
