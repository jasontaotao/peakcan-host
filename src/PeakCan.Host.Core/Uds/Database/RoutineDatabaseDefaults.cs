namespace PeakCan.Host.Core.Uds.Database;

/// <summary>
/// Default file paths and constants for <see cref="RoutineDatabase"/>.
/// </summary>
public static class RoutineDatabaseDefaults
{
    /// <summary>
    /// Default path for user-supplied routine definitions:
    /// <c>%LOCALAPPDATA%\PeakCan.Host\uds-routines.json</c>.
    /// File is optional; routines are 100% OEM-defined so an empty list is
    /// the correct state when no file is present.
    /// </summary>
    public static string DefaultJsonPath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PeakCan.Host", "uds-routines.json");
}
