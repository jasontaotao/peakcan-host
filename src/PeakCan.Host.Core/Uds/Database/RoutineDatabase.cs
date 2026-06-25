using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace PeakCan.Host.Core.Uds.Database;

/// <summary>
/// Loads UDS Routine definitions from a user JSON file at
/// <c>%APPDATA%\PeakCan.Host\uds-routines.json</c>. Routines are 100%
/// OEM-defined; there are no built-in defaults. A missing or malformed
/// JSON file does NOT throw — the database is empty and a warning is logged.
/// </summary>
public sealed partial class RoutineDatabase
{
    private readonly ILogger<RoutineDatabase>? _logger;

    /// <summary>All known routines. Empty if no user file is present.</summary>
    public IReadOnlyList<RoutineDefinition> All { get; }

    /// <summary>Create a database reading from the default user-JSON path.</summary>
    public RoutineDatabase(ILogger<RoutineDatabase>? logger = null)
        : this(RoutineDatabaseDefaults.DefaultJsonPath, logger) { }

    /// <summary>
    /// Create a database reading from <paramref name="userJsonPath"/>.
    /// Pass <c>null</c> for an empty database (no file IO).
    /// </summary>
    public RoutineDatabase(string? userJsonPath, ILogger<RoutineDatabase>? logger = null)
    {
        _logger = logger;
        All = LoadUserFile(userJsonPath).ToArray();
    }

    /// <summary>Look up a routine by its 2-byte id. Returns null if not found.</summary>
    public RoutineDefinition? Find(ushort id)
        => All.FirstOrDefault(r => r.Id == id);

    private List<RoutineDefinition> LoadUserFile(string? path)
    {
        var logger = _logger;
        if (string.IsNullOrEmpty(path))
        {
            LogNoPathConfigured(logger!);
            return new List<RoutineDefinition>();
        }

        if (!File.Exists(path))
        {
            LogFileMissing(logger!, path);
            return new List<RoutineDefinition>();
        }

        try
        {
            var json = File.ReadAllText(path);
            var dto = JsonSerializer.Deserialize<RoutineFileDto>(json, JsonOpts);
            return dto?.Routines ?? new List<RoutineDefinition>();
        }
        catch (JsonException ex)
        {
            LogMalformedJson(logger!, ex, path);
            return new List<RoutineDefinition>();
        }
        catch (IOException ex)
        {
            LogIoError(logger!, ex, path);
            return new List<RoutineDefinition>();
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new HexUshortJsonConverter() },
    };

    private sealed class RoutineFileDto
    {
        [JsonPropertyName("routines")]
        public List<RoutineDefinition> Routines { get; set; } = new();
    }

    // LoggerMessage source-generated helpers (CA1848). See DidDatabase for
    // the rationale on `ILogger` (non-nullable) parameter type and `!` at call sites.
    [LoggerMessage(Level = LogLevel.Information,
        Message = "No routine user JSON path configured.")]
    private static partial void LogNoPathConfigured(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "No routine user JSON at {Path}.")]
    private static partial void LogFileMissing(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Malformed routine JSON at {Path}; database empty.")]
    private static partial void LogMalformedJson(ILogger logger, Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "IO error reading routine JSON at {Path}; database empty.")]
    private static partial void LogIoError(ILogger logger, Exception ex, string path);
}
