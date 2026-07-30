namespace PeakCan.Host.Core.HIL;

/// <summary>
/// Parameters for InjectFault step. Adds a fault rule to the channel.
/// </summary>
public sealed record InjectFaultStep(
    CanId CanId,                   // Target CAN ID (match JSON CanIdJsonConverter format)
    Contracts.FaultType FaultType, // Enum, JSON deserialized via JsonStringEnumConverter
    double Probability,            // Drop probability (0-1)
    int DelayMs,                   // Delay in ms
    int[]? CorruptByteIndices,     // Corrupt byte positions
    byte CorruptXorMask,           // Corrupt XOR mask
    string? FaultId,               // Optional ID for targeted clearing
    Contracts.FaultDirection Direction = Contracts.FaultDirection.Send // Send/Receive/Both
) : StepParameters(TestCaseStepKind.InjectFault);
