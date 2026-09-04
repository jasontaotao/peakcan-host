using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.HIL.Core.HIL.StepExecutor;

/// <summary>
/// Executes ModifyEnvironmentFrame steps (spec §6.3 byte-level escape hatch).
/// Only for FixedHexSource targets; does not change period or bypass counter/checksum.
/// </summary>
internal sealed class ModifyEnvironmentFrameStepExecutor(Func<IEnvironmentRuntimeBridge?> bridgeFactory) : IStepExecutor
{
    public TestCaseStepKind Kind => TestCaseStepKind.ModifyEnvironmentFrame;

    public Task<StepResult> ExecuteAsync(TestCaseStep step, Contracts.IAssertionContext ctx, CancellationToken ct)
    {
        if (step.Parameters is not StepParams.ModifyEnvironmentFrameStep p)
            return Task.FromResult(new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                "Parameters is not ModifyEnvironmentFrameStep.", null, null, 0));
        try
        {
            var bridge = bridgeFactory();
            if (bridge is null)
                return Task.FromResult(new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                    "Environment runtime not available (not started).", null, null, 0));
            bridge.UpdateFrameData(p.NodeName, p.Ref, p.Data);
            return Task.FromResult(new StepResult(0, step.Kind, step.Label, StepStatus.Passed,
                $"Frame data updated for {p.NodeName}.{p.Ref}", null, null, 0));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"ModifyEnvironmentFrame failed: {ex.Message}", null, null, 0));
        }
    }
}