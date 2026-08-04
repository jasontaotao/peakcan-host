using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.Uds;

namespace PeakCan.HIL.Core.HIL.StepExecutor;

/// <summary>
/// Executes WriteDid steps. Writes raw bytes to a DID via UDS.
/// </summary>
internal sealed class WriteDidStepExecutor : IStepExecutor
{
    private readonly UdsClient _uds;

    public WriteDidStepExecutor(UdsClient uds) => _uds = uds;
    public TestCaseStepKind Kind => TestCaseStepKind.WriteDid;

    public async Task<StepResult> ExecuteAsync(TestCaseStep step, IAssertionContext ctx, CancellationToken ct)
    {
        var p = (WriteDidStep)step.Parameters;
        try
        {
            await _uds.WriteDataByIdentifierAsync(p.Did, p.Data, ct);
            return new StepResult(0, step.Kind, step.Label, StepStatus.Passed,
                $"Write DID 0x{p.Did:X4}: {Convert.ToHexString(p.Data)}", null, null, 0);
        }
        catch (UdsException ex)
        {
            return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"WriteDID 0x{p.Did:X4} failed: {ex.Message}", null, null, 0);
        }
    }
}
