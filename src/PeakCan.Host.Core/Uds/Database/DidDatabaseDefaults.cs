namespace PeakCan.Host.Core.Uds.Database;

/// <summary>
/// Default file paths and constants for <see cref="DidDatabase"/>.
/// </summary>
public static class DidDatabaseDefaults
{
    /// <summary>
    /// Default path for user-supplied DID definitions:
    /// <c>%LOCALAPPDATA%\PeakCan.Host\uds-dids.json</c>.
    /// File is optional; if missing or malformed, <see cref="DidDatabase"/>
    /// falls back to built-in defaults.
    /// </summary>
    public static string DefaultJsonPath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PeakCan.Host", "uds-dids.json");
}
