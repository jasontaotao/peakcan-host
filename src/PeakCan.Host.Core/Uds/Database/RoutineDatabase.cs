using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PeakCan.Host.Core.Path;

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
    private readonly PathOptions _options;

    /// <summary>All known routines. Empty if no user file is present.</summary>
    public IReadOnlyList<RoutineDefinition> All { get; private set; }

    /// <summary>Create a database reading from the default user-JSON path.</summary>
    public RoutineDatabase(ILogger<RoutineDatabase>? logger = null)
        : this(RoutineDatabaseDefaults.DefaultJsonPath, logger, PathOptions.Default) { }

    /// <summary>
    /// Create a database reading from <paramref name="userJsonPath"/>.
    /// Pass <c>null</c> for an empty database (no file IO).
    /// </summary>
    public RoutineDatabase(string? userJsonPath, ILogger<RoutineDatabase>? logger = null)
        : this(userJsonPath, logger, PathOptions.Default) { }

    // v1.6.10 PATCH Item 2: opt-in extension to allow config-driven
    // allowlist (replaces v1.6.4 PATCH hardcoded [LocalAppDataPeakCanRoot]).
    public RoutineDatabase(string? userJsonPath, ILogger<RoutineDatabase>? logger, PathOptions options)
    {
        _logger = logger;
        _options = options;
        All = LoadUserFile(userJsonPath).ToArray();
    }

    /// <summary>Look up a routine by its 2-byte id. Returns null if not found.</summary>
    public RoutineDefinition? Find(ushort id)
        => All.FirstOrDefault(r => r.Id == id);

    /// <summary>
    /// Append ODX-imported routine definitions. Mirrors
    /// <c>DidDatabase.AddRange</c>: NOT concurrency-safe; callers must
    /// serialize. Preserves JSON-merge constructor behavior.
    /// On duplicate <see cref="RoutineDefinition.Id"/>, last-wins +
    /// "DuplicateId" warning emitted.
    /// </summary>
    public void AddRange(IEnumerable<RoutineDefinition> defs, out IReadOnlyList<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(defs);
        var combined = All.ToList();
        var warnList = new List<string>();
        foreach (var r in defs)
        {
            var existingIdx = combined.FindIndex(x => x.Id == r.Id);
            if (existingIdx >= 0)
            {
                warnList.Add($"Duplicate Routine id 0x{r.Id:X4}; last value wins.");
                combined[existingIdx] = r;
            }
            else
            {
                combined.Add(r);
            }
        }
        All = combined.AsReadOnly();
        warnings = warnList;
    }

    private List<RoutineDefinition> LoadUserFile(string? path)
    {
        // The [LoggerMessage] source-gen helpers below require a non-null ILogger
        // argument (they dereference without a null-check). Skip logging when no
        // logger was supplied rather than calling them with a null-forced value.
        // Mirrors DidDatabase.LoadUserFile (v1.2.1 PATCH Task 4).
        if (_logger is { } l)
        {
            if (string.IsNullOrEmpty(path))
            {
                LogNoPathConfigured(l);
                return new List<RoutineDefinition>();
            }

            if (!File.Exists(path))
            {
                LogFileMissing(l, path);
                return new List<RoutineDefinition>();
            }

            try
            {
                var json = File.ReadAllText(PathNormalizer.NormalizeRestricted(path, [.. _options.AllowedRoots]));
                var dto = JsonSerializer.Deserialize<RoutineFileDto>(json, JsonOpts);
                return dto?.Routines ?? new List<RoutineDefinition>();
            }
            catch (JsonException ex)
            {
                LogMalformedJson(l, ex, path);
                return new List<RoutineDefinition>();
            }
            catch (IOException ex)
            {
                LogIoError(l, ex, path);
                return new List<RoutineDefinition>();
            }
        }

        // No logger: still execute the file-IO logic, but skip log calls.
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return new List<RoutineDefinition>();
        }

        try
        {
            var json = File.ReadAllText(PathNormalizer.NormalizeRestricted(path, [.. _options.AllowedRoots]));
            var dto = JsonSerializer.Deserialize<RoutineFileDto>(json, JsonOpts);
            return dto?.Routines ?? new List<RoutineDefinition>();
        }
        catch (JsonException)
        {
            return new List<RoutineDefinition>();
        }
        catch (IOException)
        {
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

    // LoggerMessage source-generated helpers (CA1848). Methods are not on
    // hot paths; only LoadUserFile calls them. Parameter type is non-nullable
    // ILogger because the source generator dereferences without a null check;
    // LoadUserFile guards with `if (_logger is { } l)` and passes `l` so the
    // nullable ctor parameter does not force NRE at these call sites.
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
