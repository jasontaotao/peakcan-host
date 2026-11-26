namespace PeakCan.Host.Core.Uds.Database;

/// <summary>
/// Result of the one-shot user-JSON load performed by <see cref="DidDatabase"/>
/// / <see cref="RoutineDatabase"/>. The offline report needs this to tell
/// "operator never configured a file" apart from "the file is gone" and
/// "the file failed to parse" — all three currently fall back to defaults
/// silently.
/// </summary>
public enum UserJsonLoadStatus
{
    /// <summary>No path was supplied (built-ins only).</summary>
    NotConfigured,
    /// <summary>A path was supplied but the file does not exist.</summary>
    Missing,
    /// <summary>The file was read and deserialized successfully.</summary>
    Loaded,
    /// <summary>The file exists but could not be read or parsed.</summary>
    Failed,
}
