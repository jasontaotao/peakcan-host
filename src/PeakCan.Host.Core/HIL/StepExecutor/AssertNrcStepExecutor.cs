using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.HIL.Core.HIL.StepExecutor;

/// <summary>
/// Executes AssertNrc steps. Sends a UDS request and checks if the ECU returns the expected NRC.
/// </summary>
internal sealed class AssertNrcStepExecutor : IStepExecutor
{
    private readonly IUdsSession _uds;

    public AssertNrcStepExecutor(IUdsSession uds) => _uds = uds;
    public TestCaseStepKind Kind => TestCaseStepKind.AssertNrc;

    public async Task<StepResult> ExecuteAsync(TestCaseStep step, IAssertionContext ctx, CancellationToken ct)
    {
        var p = (AssertNrcStep)step.Parameters;
        try
        {
            await _uds.SendRequestAsync(p.ServiceId, p.Data, ct);
            // Positive response (no exception) → we expected NRC → fail
            return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"Expected NRC 0x{p.ExpectedNrc:X2} but got positive response for service 0x{p.ServiceId:X2}",
                "positive response", $"NRC 0x{p.ExpectedNrc:X2}", 0);
        }
        catch (UdsNrcException ex)
        {
            bool nrcMatches = ex.Nrc == p.ExpectedNrc;
            return new StepResult(0, step.Kind, step.Label,
                nrcMatches ? StepStatus.Passed : StepStatus.Failed,
                nrcMatches ? $"NRC 0x{p.ExpectedNrc:X2} received as expected"
                           : $"NRC mismatch: got 0x{ex.Nrc:X2}, expected 0x{p.ExpectedNrc:X2}",
                $"0x{ex.Nrc:X2}", $"0x{p.ExpectedNrc:X2}", 0);
        }
        catch (UdsSessionException ex)
        {
            return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                $"UDS error (not NRC): {ex.Message}", null, null, 0);
        }
    }
}
