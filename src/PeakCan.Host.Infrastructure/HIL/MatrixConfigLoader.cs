using System.Text.Json;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.Host.Infrastructure.HIL;

/// <summary>
/// Loads a multi-ECU matrix configuration from JSON.
/// Supports inline ECU definitions and external scriptPath references.
/// </summary>
public static class MatrixConfigLoader
{
    /// <summary>
    /// Parse matrix config from JSON string.
    /// </summary>
    /// <param name="json">Matrix JSON string.</param>
    /// <param name="basePath">Base directory for resolving relative scriptPath references. Null = current directory.</param>
    /// <param name="externalGenerators">Phase 7 Unit B: external plugin generators passed to each ECU script (optional).</param>
    public static MatrixConfig Parse(string json, string? basePath = null,
        IEnumerable<IEcuResponseGenerator>? externalGenerators = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var name = root.GetProperty("name").GetString()!;
        var ecus = new List<EcuScript>();

        foreach (var ecuEl in root.GetProperty("ecus").EnumerateArray())
        {
            if (ecuEl.TryGetProperty("scriptPath", out var scriptPathEl))
            {
                // External reference: load ECU script from file
                var scriptPath = scriptPathEl.GetString()!;
                var fullPath = basePath is not null
                    ? Path.Combine(basePath, scriptPath)
                    : scriptPath;

                // Prevent path traversal: resolved path must stay within basePath
                if (basePath is not null)
                {
                    var baseFullPath = Path.GetFullPath(basePath);
                    var resolvedFullPath = Path.GetFullPath(fullPath);
                    if (!resolvedFullPath.StartsWith(baseFullPath + Path.DirectorySeparatorChar)
                        && resolvedFullPath != baseFullPath)
                    {
                        throw new InvalidOperationException(
                            $"scriptPath escapes base directory: {scriptPath}");
                    }
                }

                ecus.Add(EcuScriptLoader.Load(fullPath, externalGenerators));
            }
            else
            {
                // Inline ECU definition
                ecus.Add(EcuScriptLoader.ParseEcuScript(ecuEl, externalGenerators));
            }
        }

        return new MatrixConfig(name, ecus);
    }

    /// <summary>
    /// Load matrix config from a JSON file path. Resolves scriptPath references
    /// relative to the matrix file's directory.
    /// </summary>
    public static MatrixConfig LoadFromFile(string path,
        IEnumerable<IEcuResponseGenerator>? externalGenerators = null)
    {
        var json = File.ReadAllText(path);
        var basePath = Path.GetDirectoryName(Path.GetFullPath(path));
        return Parse(json, basePath, externalGenerators);
    }

    /// <summary>
    /// Load matrix config from a JSON file path (backward compat).
    /// </summary>
    public static MatrixConfig Load(string path,
        IEnumerable<IEcuResponseGenerator>? externalGenerators = null)
        => LoadFromFile(path, externalGenerators);
}
