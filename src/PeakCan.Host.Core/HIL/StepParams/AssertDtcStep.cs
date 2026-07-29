namespace PeakCan.Host.Core.HIL;

/// <summary>
/// Parameters for asserting DTC presence/absence.
/// </summary>
public record AssertDtcStep(ushort? DtcCode, bool ExpectPresent)
    : StepParameters(TestCaseStepKind.AssertDtc);
