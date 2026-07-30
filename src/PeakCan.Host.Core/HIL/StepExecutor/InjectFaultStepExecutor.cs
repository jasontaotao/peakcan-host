using PeakCan.Host.Core.HIL.Contracts;

namespace PeakCan.Host.Core.HIL.StepExecutor;

/// <summary>
/// Executes InjectFault steps. Adds a fault rule to the channel via IFaultInjectionContext.
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

        var handle = faultCtx.AddFault(rule);

        if (p.FaultId is not null)
            faultCtx.TagFault(p.FaultId, handle);

        return Task.FromResult(new StepResult(0, step.Kind, step.Label, StepStatus.Passed,
            $"Fault injected: {p.FaultType}", null, null, 0));
    }
}
