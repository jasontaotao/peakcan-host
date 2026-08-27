using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.Uds;

namespace PeakCan.HIL.Core.HIL.StepExecutor;

/// <summary>
/// Executes IOControl (0x2F) steps; result bytes stored to IStepVariableStore
/// when OutputVar is set (ReadDidStepExecutor pattern).
/// </summary>
internal sealed class IOControlStepExecutor : IStepExecutor
{
    private readonly IUdsSessionResolver _resolver;

    public IOControlStepExecutor(IUdsSessionResolver resolver) => _resolver = resolver;
    public TestCaseStepKind Kind => TestCaseStepKind.IOControl;

    public async Task<StepResult> ExecuteAsync(TestCaseStep step, IAssertionContext ctx, CancellationToken ct)
    {
        var p = (IOControlStep)step.Parameters;
        var session = _resolver.Resolve(p.TargetChannel);
        try
        {
            var result = await session.IOControlAsync(p.Did, p.ControlType, p.Data, ct: ct);
            if (p.OutputVar is { } varName && ctx is IStepVariableStore store)
                store.Variables[varName] = result;
            return new StepResult(0, step.Kind, step.Label, StepStatus.Passed,
                result.Length > 0
                    ? $"IOControl 0x{p.Did:X4} type {p.ControlType}: {Convert.ToHexString(result)}"
                    : $"IOControl 0x{p.Did:X4} type {p.ControlType}: OK", null, null, 0, Channel: p.TargetChannel);
        }
        catch (UdsSessionException ex)
        {
            return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"IOControl 0x{p.Did:X4} failed: {ex.Message}", null, null, 0, Channel: p.TargetChannel);
        }
    }
}
