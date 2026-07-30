using System.Text.Json;

namespace PeakCan.Host.Infrastructure.HIL;

/// <summary>
/// Loads a multi-ECU matrix configuration from JSON.
/// Supports inline ECU definitions (external file references deferred to future).
/// </summary>
public static class MatrixConfigLoader
{
    /// <summary>
    /// Load matrix config from a JSON file path.
    /// </summary>
    public static MatrixConfig Load(string path)
    {
        var json = File.ReadAllText(path);
        return Parse(json);
    }

    /// <summary>
    /// Parse matrix config from JSON string.
    /// </summary>
    public static MatrixConfig Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var name = root.GetProperty("name").GetString()!;
        var ecus = new List<EcuScript>();

        foreach (var ecuEl in root.GetProperty("ecus").EnumerateArray())
        {
            ecus.Add(EcuScriptLoader.ParseEcuScript(ecuEl));
        }

        return new MatrixConfig(name, ecus);
    }
}
