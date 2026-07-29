namespace PeakCan.Host.Core.HIL;

/// <summary>
/// Parameters for waiting until a signal reaches expected value within tolerance.
/// </summary>
public record WaitForSignalStep(string SignalName, double Expected, double Tolerance, int TimeoutMs)
    : StepParameters(TestCaseStepKind.WaitForSignal);
