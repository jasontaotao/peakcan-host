namespace PeakCan.Host.Core.HIL;

/// <summary>
/// Parameters for asserting a signal value immediately (no wait).
/// </summary>
public record AssertSignalStep(string SignalName, double Expected, double Tolerance)
    : StepParameters(TestCaseStepKind.AssertSignal);
