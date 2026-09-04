using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.HIL.Core.HIL.StepExecutor;

/// <summary>
/// Executes SetEnvironmentSignal steps (spec §6.3 signal-level primary form).
/// Calls the environment runtime bridge to update the signal state.
/// </summary>
internal sealed class SetEnvironmentSignalStepExecutor(Func<IEnvironmentRuntimeBridge?> bridgeFactory) : IStepExecutor
{
    public TestCaseStepKind Kind => TestCaseStepKind.SetEnvironmentSignal;

    public Task<StepResult> ExecuteAsync(TestCaseStep step, Contracts.IAssertionContext ctx, CancellationToken ct)
    {
        if (step.Parameters is not StepParams.SetEnvironmentSignalStep p)
            return Task.FromResult(new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                "Parameters is not SetEnvironmentSignalStep.", null, null, 0));
        try
        {
            bridgeFactory()?.SetSignalValue(p.NodeName, p.MessageName, p.SignalName, p.Value);
            return Task.FromResult(new StepResult(0, step.Kind, step.Label, StepStatus.Passed,
                $"Signal {p.NodeName}.{p.MessageName}.{p.SignalName} = {p.Value}", null, null, 0));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"SetEnvironmentSignal failed: {ex.Message}", null, null, 0));
        }
    }
}