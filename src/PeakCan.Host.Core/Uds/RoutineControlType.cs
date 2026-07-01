namespace PeakCan.Host.Core.Uds;

/// <summary>
/// v1.3.0 MINOR Item 3/4: ISO 14229-1 §10.4 RoutineControl (0x31) sub-functions.
/// Standard values only; OEM-specific range (0x40-0x7F) is reachable via
/// the <c>byte</c> overload of <c>RoutineControlAsync</c>.
/// </summary>
public enum RoutineControlType : byte
{
    /// <summary>Start the routine identified by routineId.</summary>
    StartRoutine = 0x01,

    /// <summary>Stop the routine identified by routineId.</summary>
    StopRoutine = 0x02,

    /// <summary>Request results from the routine identified by routineId.</summary>
    RequestRoutineResults = 0x03,
}
