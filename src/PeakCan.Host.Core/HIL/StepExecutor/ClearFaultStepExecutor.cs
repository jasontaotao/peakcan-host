using PeakCan.Host.Core.HIL.Contracts;

namespace PeakCan.Host.Core.HIL.StepExecutor;

/// <summary>
/// Executes ClearFault steps. Removes fault rules from the channel via IFaultInjectionContext.
/// </summary>
public sealed class ClearFaultStepExecutor : IStepExecutor
{
    public TestCaseStepKind Kind => TestCaseStepKind.ClearFault;

    public Task<StepResult> ExecuteAsync(TestCaseStep step, IAssertionContext ctx, CancellationToken ct)
    {
        if (ctx is not IFaultInjectionContext faultCtx)
            return Task.FromResult(new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                "Context does not support fault injection", null, null, 0));

        var p = (ClearFaultStep)step.Parameters;
        faultCtx.ClearFaults(p.FaultId);

        return Task.FromResult(new StepResult(0, step.Kind, step.Label, StepStatus.Passed,
            p.FaultId is null ? "All faults cleared" : $"Fault '{p.FaultId}' cleared", null, null, 0));
    }
}
