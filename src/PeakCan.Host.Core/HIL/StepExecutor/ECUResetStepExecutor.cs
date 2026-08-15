using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.Uds;

namespace PeakCan.HIL.Core.HIL.StepExecutor;

/// <summary>
/// Executes ECUReset (0x11) steps. Sends reset, then polls TesterPresent
/// until the ECU responds again (reconnect phase) or ReconnectTimeoutMs elapses.
/// </summary>
internal sealed class ECUResetStepExecutor : IStepExecutor
{
    private const int ReconnectTimeoutMs = 5000;

    private readonly UdsClient _uds;

    public ECUResetStepExecutor(UdsClient uds) => _uds = uds;
    public TestCaseStepKind Kind => TestCaseStepKind.ECUReset;

    public async Task<StepResult> ExecuteAsync(TestCaseStep step, IAssertionContext ctx, CancellationToken ct)
    {
        var p = (ECUResetStep)step.Parameters;
        try
        {
            await _uds.EcuResetAsync(p.ResetType, ct);
            await WaitForReconnectAsync(ct);
            return new StepResult(0, step.Kind, step.Label, StepStatus.Passed,
                $"ECUReset 0x{p.ResetType:X2} complete", null, null, 0);
        }
        catch (UdsException ex)
        {
            return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"ECUReset 0x{p.ResetType:X2} failed: {ex.Message}", null, null, 0);
        }
    }

    private async Task WaitForReconnectAsync(CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + ReconnectTimeoutMs;
        while (!ct.IsCancellationRequested && Environment.TickCount64 < deadline)
        {
            try { await _uds.TesterPresentAsync(suppressPosResponse: true, ct); return; }
            catch (UdsException) { await Task.Delay(200, ct); }
        }
    }
}
