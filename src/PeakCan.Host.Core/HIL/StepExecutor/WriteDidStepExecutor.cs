using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.HIL.Core.HIL.StepExecutor;

/// <summary>
/// Executes WriteDid steps. Writes raw bytes to a DID via UDS.
/// Task B 第一步（Q1，spec 2026-08-27）：依赖 IUdsSession 接口而非 concrete UdsClient
/// （多通道路由 IUdsSessionResolver 的前置统一）。异常契约同 ReadDidStepExecutor。
/// </summary>
internal sealed class WriteDidStepExecutor : IStepExecutor
{
    private readonly IUdsSession _uds;

    public WriteDidStepExecutor(IUdsSession uds) => _uds = uds;
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
        catch (UdsSessionException ex)
        {
            return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"WriteDID 0x{p.Did:X4} failed: {ex.Message}", null, null, 0);
        }
    }
}
