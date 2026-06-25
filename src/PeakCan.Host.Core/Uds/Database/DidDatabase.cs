using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace PeakCan.Host.Core.Uds.Database;

/// <summary>
/// Loads UDS Data Identifier (DID) definitions. Sources, in priority order:
/// built-in defaults → user JSON file at
/// <c>%APPDATA%\PeakCan.Host\uds-dids.json</c>. User entries with matching
/// <see cref="DidDefinition.Id"/> override built-ins; non-matching entries
/// are appended. A missing or malformed JSON file does NOT throw — the
/// built-in defaults are used and a warning is logged.
/// </summary>
public sealed partial class DidDatabase
{
    private readonly ILogger<DidDatabase>? _logger;

    /// <summary>All known DIDs (built-in + user), with user overrides applied.</summary>
    public IReadOnlyList<DidDefinition> All { get; }

    /// <summary>Create a database reading from the default user-JSON path.</summary>
    public DidDatabase(ILogger<DidDatabase>? logger = null)
        : this(DidDatabaseDefaults.DefaultJsonPath, logger) { }

    /// <summary>
    /// Create a database reading from <paramref name="userJsonPath"/>.
    /// Pass <c>null</c> to skip user-JSON entirely (built-in only).
    /// </summary>
    public DidDatabase(string? userJsonPath, ILogger<DidDatabase>? logger = null)
    {
        _logger = logger;
        var builtIn = BuiltInDefaults();
        var user = LoadUserFile(userJsonPath);
        All = MergeBuiltInAndUser(builtIn, user);
    }

    /// <summary>Look up a DID by its 2-byte id. Returns null if not found.</summary>
    public DidDefinition? Find(ushort id)
        => All.FirstOrDefault(d => d.Id == id);

    private static DidDefinition[] BuiltInDefaults() => new DidDefinition[]
    {
        new(0xF190, "VIN",             "Vehicle Identification Number", 17, false),
        new(0xF187, "PartNumber",      "ECU Part Number",               10, false),
        new(0xF18A, "SupplierID",      "ECU Supplier ID",                4, false),
        new(0xF191, "HardwareVersion", "ECU Hardware Version",           3, false),
        new(0xF184, "SoftwareVersion", "ECU Software Version",           9, false),
    };

    private List<DidDefinition>? LoadUserFile(string? path)
    {
        // The [LoggerMessage] source-gen helpers below require a non-null ILogger
        // argument (they dereference without a null-check). Skip logging when no
        // logger was supplied rather than calling them with a null-forced value.
        if (_logger is { } l)
        {
            if (string.IsNullOrEmpty(path))
            {
                LogNoPathConfigured(l);
                return null;
            }

            if (!File.Exists(path))
            {
                LogFileMissing(l, path);
                return null;
            }

            try
            {
                var json = File.ReadAllText(path);
                var dto = JsonSerializer.Deserialize<DidFileDto>(json, JsonOpts);
                return dto?.Dids;
            }
            catch (JsonException ex)
            {
                LogMalformedJson(l, ex, path);
                return null;
            }
            catch (IOException ex)
            {
                LogIoError(l, ex, path);
                return null;
            }
        }

        // No logger: still execute the file-IO logic, but skip log calls.
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            var dto = JsonSerializer.Deserialize<DidFileDto>(json, JsonOpts);
            return dto?.Dids;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static DidDefinition[] MergeBuiltInAndUser(
        DidDefinition[] builtIn,
        List<DidDefinition>? user)
    {
        if (user is null)
            return builtIn;

        var userIds = new HashSet<ushort>(user.Select(d => d.Id));
        var result = new List<DidDefinition>(builtIn.Length + user.Count);
        // Built-ins that are NOT overridden by user entries first, in original order.
        foreach (var d in builtIn)
            if (!userIds.Contains(d.Id))
                result.Add(d);
        // Then all user entries (which include the overrides).
        result.AddRange(user);
        return result.ToArray();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new HexUshortJsonConverter() },
    };

    private sealed class DidFileDto
    {
        [JsonPropertyName("dids")]
        public List<DidDefinition> Dids { get; set; } = new();
    }

    // LoggerMessage source-generated helpers (CA1848). Methods are not on
    // hot paths; only LoadUserFile calls them. Parameter type is non-nullable
    // ILogger because the source generator dereferences without a null check;
    // LoadUserFile guards with `if (_logger is { } l)` and passes `l` so the
    // nullable ctor parameter does not force NRE at these call sites.
    [LoggerMessage(Level = LogLevel.Information,
        Message = "No DID user JSON path configured; using built-in defaults only.")]
    private static partial void LogNoPathConfigured(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "No DID user JSON at {Path}; using built-in defaults.")]
    private static partial void LogFileMissing(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Malformed DID JSON at {Path}; using built-in defaults.")]
    private static partial void LogMalformedJson(ILogger logger, Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "IO error reading DID JSON at {Path}; using built-in defaults.")]
    private static partial void LogIoError(ILogger logger, Exception ex, string path);
}
