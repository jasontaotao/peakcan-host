namespace PeakCan.Host.Core.HIL;

/// <summary>
/// Parameters for a fixed delay.
/// </summary>
public record DelayStep(int Milliseconds) : StepParameters(TestCaseStepKind.Delay);
