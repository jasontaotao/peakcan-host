using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OxyPlot;

namespace PeakCan.Host.App.Services.Trace;

/// <summary>
/// v3.5.0 MINOR: persists a multi-trace Trace Viewer session to a
/// <c>.tmtrace</c> file. Atomic write (tmp + rename) so a crash mid-write
/// leaves the previous good copy intact. Missing or corrupt files load
/// as <c>null</c> (logged at Error) — caller is responsible for showing
/// a <c>MessageBox</c> and continuing.
/// <para>
/// Adapted from <c>SequenceLibrary</c> (v2.1.2 PATCH): the file envelope
/// pattern (version + schema + JSON body + UTF-8 BOM + atomic rename +
/// corrupt-recovery-to-empty) is the same. The contents shape is
/// distinct (single bundle, not a list of named entities).
/// </para>
/// </summary>
public sealed partial class TraceSessionLibrary
{
    /// <summary>
    /// The current bundle schema identifier. Bumped when fields are
    /// renamed/removed (additions are non-breaking under
    /// <see cref="JsonSerializerOptions.PropertyNameCaseInsensitive"/>
    /// + skip-unknown behavior).
    /// </summary>
    public const string CurrentSchema = "tmtrace/v1";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters =
        {
            new OxyColorJsonConverter(),
            new JsonStringEnumConverter(),
        },
    };

    private readonly string _path;
    private readonly ILogger<TraceSessionLibrary> _logger;

    /// <summary>Production ctor — uses the ctor-supplied path. Default
    /// location is left to the caller (the ViewModel picks the save-as
    /// dialog target).</summary>
    public TraceSessionLibrary(ILogger<TraceSessionLibrary> logger)
        : this(DefaultPath(), logger) { }

    /// <summary>Test ctor with custom path.</summary>
    internal TraceSessionLibrary(string path, ILogger<TraceSessionLibrary> logger)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    /// <summary>
    /// Read a bundle from <paramref name="path"/>. Returns <c>null</c> if
    /// the file is missing or corrupt (logged at Error). Callers should
    /// show a <c>MessageBox</c> on null returns from non-empty paths so
    /// users can investigate via the log file.
    /// </summary>
    public TraceSessionBundleDto? Load(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        if (!File.Exists(path))
            return null;
        try
        {
            var json = File.ReadAllText(path);
            var dto = JsonSerializer.Deserialize<TraceSessionBundleDto>(json, JsonOpts);
            return dto;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            LogCorrupt(_logger, path, ex);
            return null;
        }
    }

    /// <summary>
    /// Persist <paramref name="snapshot"/> atomically: write to
    /// <c>{path}.tmp</c>, then rename over <c>{path}</c>. A crash
    /// mid-rename leaves either the old file or the new file — never a
    /// half-written one. On failure, the tmp file is cleaned up and the
    /// exception rethrown with the original path attached.
    /// </summary>
    public void Save(TraceSessionBundleDto snapshot, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var target = path ?? _path;
        var tmp = target + ".tmp";
        try
        {
            snapshot.Schema = CurrentSchema;
            snapshot.SavedAt = DateTimeOffset.UtcNow;
            var json = JsonSerializer.Serialize(snapshot, JsonOpts);
            File.WriteAllText(tmp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            // Atomic on POSIX (rename) and Windows (MoveFileEx
            // MOVEFILE_REPLACE_EXISTING) — same pattern as SequenceLibrary
            // v2.1.2 PATCH and SendFrameLibrary v1.2.13 PATCH Item 8.
            File.Move(tmp, target, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            LogSaveFailed(_logger, ex, target);
            throw;
        }
    }

    private static string DefaultPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "PeakCan.Host", "trace-session.tmtrace");
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Trace session bundle corrupt or unreadable: {Path}")]
    private static partial void LogCorrupt(ILogger logger, string path, Exception ex);

    [LoggerMessage(EventId = 9001, Level = LogLevel.Error, Message = "TraceSessionLibrary save to {Path} failed")]
    private static partial void LogSaveFailed(ILogger logger, Exception ex, string path);
}