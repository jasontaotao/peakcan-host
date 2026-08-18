using System.Globalization;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.Uds;

namespace PeakCan.HIL.Core.HIL.StepExecutor;

/// <summary>
/// Executes ClearDtc steps. Clears DTCs in a group via UDS.
/// B.5: Group is now string hex (supports ${name} interpolation).
/// </summary>
internal sealed class ClearDtcStepExecutor : IStepExecutor
{
    private readonly UdsClient _uds;

    public ClearDtcStepExecutor(UdsClient uds) => _uds = uds;
    public TestCaseStepKind Kind => TestCaseStepKind.ClearDtc;

    public async Task<StepResult> ExecuteAsync(TestCaseStep step, IAssertionContext ctx, CancellationToken ct)
    {
        var p = (ClearDtcStep)step.Parameters;
        // B.5: Group is now string (hex like "0xFFFFFF" or interpolated "${param.group}")
        var groupStr = p.Group.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? p.Group[2..] : p.Group;
        var group = uint.Parse(groupStr, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        try
        {
            await _uds.ClearDiagnosticInformationAsync(group, ct);
            return new StepResult(0, step.Kind, step.Label, StepStatus.Passed,
                group == 0xFFFFFF
                    ? "Cleared all DTCs"
                    : $"Cleared DTC group 0x{group:X6}", null, null, 0);
        }
        catch (UdsException ex)
        {
            return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"ClearDtc failed: {ex.Message}", null, null, 0);
        }
    }
}
