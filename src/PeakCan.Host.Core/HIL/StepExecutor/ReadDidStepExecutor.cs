using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.Uds;

namespace PeakCan.HIL.Core.HIL.StepExecutor;

/// <summary>
/// Executes ReadDid steps. Reads a DID via UDS and stores the bytes into
/// <see cref="IStepVariableStore"/> for a later AssertDidValue step.
/// </summary>
internal sealed class ReadDidStepExecutor : IStepExecutor
{
    private readonly UdsClient _uds;

    public ReadDidStepExecutor(UdsClient uds) => _uds = uds;
    public TestCaseStepKind Kind => TestCaseStepKind.ReadDid;

    public async Task<StepResult> ExecuteAsync(TestCaseStep step, IAssertionContext ctx, CancellationToken ct)
    {
        var p = (ReadDidStep)step.Parameters;
        try
        {
            // UDS 超时由 UdsTimer（P2/P2*）管理，不传 timeoutMs；取消经 ct
            var data = await _uds.ReadDataByIdentifierAsync(p.Did, ct);
            var key = p.OutputVar ?? $"did_0x{p.Did:X4}";
            if (ctx is IStepVariableStore store)
                store.Variables[key] = data;
            return new StepResult(0, step.Kind, step.Label, StepStatus.Passed,
                $"Read DID 0x{p.Did:X4}: {Convert.ToHexString(data)}", null, null, 0);
        }
        catch (UdsException ex)   // NRC / security-lockout 均派生自 UdsException
        {
            return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"ReadDID 0x{p.Did:X4} failed: {ex.Message}", null, null, 0);
        }
    }
}
