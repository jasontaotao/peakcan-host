namespace PeakCan.Host.Core.HIL;

/// <summary>
/// Parameters for waiting until a specific CAN frame appears.
/// </summary>
public record ExpectFrameStep(CanId Id, byte[]? DataMask, int TimeoutMs)
    : StepParameters(TestCaseStepKind.WaitForFrame);
