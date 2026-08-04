using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.HIL.Core.HIL.StepExecutor;

/// <summary>
/// Executes InjectFault steps. Adds a fault rule to the channel via IFaultInjectionContext.
/// Supports Send, Receive, and Both directions.
/// </summary>
public sealed class InjectFaultStepExecutor : IStepExecutor
{
    public TestCaseStepKind Kind => TestCaseStepKind.InjectFault;

    public Task<StepResult> ExecuteAsync(TestCaseStep step, IAssertionContext ctx, CancellationToken ct)
    {
        if (ctx is not IFaultInjectionContext faultCtx)
            return Task.FromResult(new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                "Context does not support fault injection", null, null, 0));

        var p = (InjectFaultStep)step.Parameters;
        var rule = new FaultRule
        {
            Type = p.FaultType,
            TargetCanId = p.CanId.Raw == 0 ? null : p.CanId.Raw,
            Probability = p.Probability,
            DelayMs = p.DelayMs,
            CorruptByteIndices = p.CorruptByteIndices,
            CorruptXorMask = p.CorruptXorMask,
        };

        IDisposable? sendHandle = null;
        IDisposable? recvHandle = null;

        switch (p.Direction)
        {
            case FaultDirection.Send:
                sendHandle = faultCtx.AddFault(rule);
                break;
            case FaultDirection.Receive:
                recvHandle = faultCtx.AddReceiveFault(rule);
                break;
            case FaultDirection.Both:
                sendHandle = faultCtx.AddFault(rule);
                recvHandle = faultCtx.AddReceiveFault(rule);
                break;
        }

        if (p.FaultId is not null)
        {
            if (sendHandle is not null) faultCtx.TagFault(p.FaultId + "_tx", sendHandle);
            if (recvHandle is not null) faultCtx.TagFault(p.FaultId + "_rx", recvHandle);
        }

        return Task.FromResult(new StepResult(0, step.Kind, step.Label, StepStatus.Passed,
            $"Fault injected: {p.FaultType} ({p.Direction})", null, null, 0));
    }
}
