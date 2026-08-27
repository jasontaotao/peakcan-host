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

    private readonly IUdsSessionResolver _resolver;
    private readonly int _reconnectTimeoutMs;

    public ECUResetStepExecutor(IUdsSessionResolver resolver) : this(resolver, DefaultReconnectTimeoutMs) { }

    /// <summary>
    /// Test seam: lets tests exercise the reconnect-timeout path without a
    /// 5-second wall-clock wait. DI keeps using the public 1-arg ctor.
    /// </summary>
    internal ECUResetStepExecutor(IUdsSessionResolver resolver, int reconnectTimeoutMs)
    {
        _resolver = resolver;
        _reconnectTimeoutMs = reconnectTimeoutMs;
    }

    public TestCaseStepKind Kind => TestCaseStepKind.ECUReset;

    public async Task<StepResult> ExecuteAsync(TestCaseStep step, IAssertionContext ctx, CancellationToken ct)
    {
        var p = (ECUResetStep)step.Parameters;
        var session = _resolver.Resolve(p.TargetChannel);
        try
        {
            await session.EcuResetAsync(p.ResetType, ct);
            if (!await WaitForReconnectAsync(session, ct))
                return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                    $"ECUReset 0x{p.ResetType:X2} sent but ECU did not reconnect within {_reconnectTimeoutMs}ms",
                    null, null, 0, Channel: p.TargetChannel);
            return new StepResult(0, step.Kind, step.Label, StepStatus.Passed,
                $"ECUReset 0x{p.ResetType:X2} complete", null, null, 0, Channel: p.TargetChannel);
        }
        catch (UdsSessionException ex)
        {
            return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"ECUReset 0x{p.ResetType:X2} failed: {ex.Message}", null, null, 0, Channel: p.TargetChannel);
        }
    }

    private async Task<bool> WaitForReconnectAsync(IUdsSession session, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + _reconnectTimeoutMs;
        while (!ct.IsCancellationRequested && Environment.TickCount64 < deadline)
        {
            try { await session.TesterPresentAsync(suppressPosResponse: true, ct); return true; }
            catch (UdsSessionException) { await Task.Delay(200, ct); }
        }

        return false;
    }
}
