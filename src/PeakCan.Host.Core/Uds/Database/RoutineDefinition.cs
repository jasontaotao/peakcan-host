namespace PeakCan.Host.Core.Uds.Database;

/// <summary>
/// Definition of a single UDS Routine (0x31). Routines are 100% OEM-defined;
/// there are no built-in defaults. Users populate via
/// <c>%APPDATA%\PeakCan.Host\uds-routines.json</c>.
/// </summary>
/// <param name="Id">2-byte routine ID.</param>
/// <param name="Name">Short human-readable name.</param>
/// <param name="Description">Longer description for UI.</param>
/// <param name="Startable">Whether <c>RoutineControl (0x31, 0x01)</c> is supported.</param>
/// <param name="Stoppable">Whether <c>RoutineControl (0x31, 0x02)</c> is supported.</param>
public sealed record RoutineDefinition(
    ushort Id,
    string Name,
    string Description,
    bool Startable,
    bool Stoppable);
