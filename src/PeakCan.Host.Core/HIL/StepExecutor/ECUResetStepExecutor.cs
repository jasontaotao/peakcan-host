using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.Uds;

namespace PeakCan.HIL.Core.HIL.StepExecutor;

/// <summary>
/// Executes ECUReset (0x11) steps. Sends reset, then polls TesterPresent
/// until the ECU responds again (reconnect phase) or the reconnect timeout
/// elapses. A reset that acknowledges but never comes back online within the
/// window is reported as Failed — the reconnect is the confirmation that the
/// reset actually took effect.
/// </summary>
internal sealed class ECUResetStepExecutor : IStepExecutor
{
    private const int DefaultReconnectTimeoutMs = 5000;

    private readonly UdsClient _uds;
    private readonly int _reconnectTimeoutMs;

    public ECUResetStepExecutor(UdsClient uds) : this(uds, DefaultReconnectTimeoutMs) { }

    /// <summary>
    /// Test seam: lets tests exercise the reconnect-timeout path without a
    /// 5-second wall-clock wait. DI keeps using the public 1-arg ctor.
    /// </summary>
    internal ECUResetStepExecutor(UdsClient uds, int reconnectTimeoutMs)
    {
        _uds = uds;
        _reconnectTimeoutMs = reconnectTimeoutMs;
    }

    public TestCaseStepKind Kind => TestCaseStepKind.ECUReset;

    public async Task<StepResult> ExecuteAsync(TestCaseStep step, IAssertionContext ctx, CancellationToken ct)
    {
        var p = (ECUResetStep)step.Parameters;
        try
        {
            await _uds.EcuResetAsync(p.ResetType, ct);
            if (!await WaitForReconnectAsync(ct))
                return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                    $"ECUReset 0x{p.ResetType:X2} sent but ECU did not reconnect within {_reconnectTimeoutMs}ms",
                    null, null, 0);
            return new StepResult(0, step.Kind, step.Label, StepStatus.Passed,
                $"ECUReset 0x{p.ResetType:X2} complete", null, null, 0);
        }
        catch (UdsException ex)
        {
            return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"ECUReset 0x{p.ResetType:X2} failed: {ex.Message}", null, null, 0);
        }
    }

    private async Task<bool> WaitForReconnectAsync(CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + _reconnectTimeoutMs;
        while (!ct.IsCancellationRequested && Environment.TickCount64 < deadline)
        {
            try { await _uds.TesterPresentAsync(suppressPosResponse: true, ct); return true; }
            catch (UdsException) { await Task.Delay(200, ct); }
        }

        return false;
    }
}
