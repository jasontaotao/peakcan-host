namespace PeakCan.Host.Core.HIL;

/// <summary>
/// Parameters for asserting response time between request and response frames.
/// </summary>
public record AssertResponseTimeStep(CanId ReqId, CanId RespId, int MaxMs)
    : StepParameters(TestCaseStepKind.AssertResponseTime);
