namespace PeakCan.Host.Core.HIL;

/// <summary>
/// Parameters for ClearFault step. Removes fault rules from the channel.
/// </summary>
public sealed record ClearFaultStep(
    string? FaultId    // null = clear all faults, non-null = clear only matching ID
) : StepParameters(TestCaseStepKind.ClearFault);
