namespace PeakCan.Host.Core.HIL;

/// <summary>
/// Parameters for sending a single CAN frame.
/// </summary>
public record SendFrameStep(CanId Id, byte[] Data, bool Fd, bool Extended)
    : StepParameters(TestCaseStepKind.SendFrame);
