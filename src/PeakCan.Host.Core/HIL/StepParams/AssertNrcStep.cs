namespace PeakCan.Host.Core.HIL;

/// <summary>
/// Parameters for asserting a specific Negative Response Code.
/// </summary>
public record AssertNrcStep(byte ServiceId, byte ExpectedNrc)
    : StepParameters(TestCaseStepKind.AssertNrc);
