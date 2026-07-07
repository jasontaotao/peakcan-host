using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OxyPlot;
using PeakCan.Host.Core.Path;

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

    /// <summary>
    /// v3.10.0 MINOR T3 (H4): hard cap on .tmtrace file size the
    /// loader is willing to read+deserialize. Mirrors
    /// <see cref="RecentSessionsService.MaxLoadFileBytes"/> pattern
    /// (v3.8.8 PATCH F2). 50 MB is far above any legitimate bundle
    /// (200 sources + 1000 bookmarks ≈ 200 KB) and gives 250x headroom
    /// for future growth.
    /// </summary>
    public const int MaxLoadFileBytes = 50 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        MaxDepth = 64,                                       // v3.10.0 T3 (H4 + L7)
        ReferenceHandler = ReferenceHandler.IgnoreCycles,   // v3.10.0 T3
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
    /// the file is missing, oversized, traversal-style, or corrupt
    /// (logged at Error or Warning). Callers should show a
    /// <c>MessageBox</c> on null returns from non-empty paths so users
    /// can investigate via the log file.
    /// </summary>
    public TraceSessionBundleDto? Load(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        // v3.10.0 MINOR T3 (H4): defense-in-depth path validation.
        // Mirrors TraceViewerService.LoadAsync (v3.9.1) pattern. Reject
        // relative paths + traversal segments + null bytes at the loader
        // boundary, keeping the corrupt-recovery contract — never throw.
        string normalized;
        try
        {
            normalized = PathNormalizer.Normalize(path);
        }
        catch (PathNormalizationException ex)
        {
            LogCorrupt(_logger, path, ex);
            return null;
        }
        if (!File.Exists(normalized))
            return null;
        // v3.10.0 MINOR T3 (H4): oversized-file precheck. Without this
        // guard, a user who drops a large file at the persisted path
        // would block the WPF dispatcher for the full File.ReadAllText +
        // JsonSerializer.Deserialize duration — a 1 GB file risks OOM.
        // Refuse to deserialize anything beyond MaxLoadFileBytes and
        // treat as corrupt — same defensive contract as
        // RecentSessionsService.LoadAsync (v3.8.8 PATCH F2).
        var info = new FileInfo(normalized);
        if (info.Length > MaxLoadFileBytes)
        {
            LogOversized(_logger, normalized, info.Length, MaxLoadFileBytes);
            return null;
        }
        try
        {
            var json = File.ReadAllText(normalized);
            var dto = JsonSerializer.Deserialize<TraceSessionBundleDto>(json, JsonOpts);
            return dto;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            LogCorrupt(_logger, normalized, ex);
            return null;
        }
    }

    /// <summary>
    /// Persist <paramref name="snapshot"/> atomically: write to
    /// <c>{path}.tmp</c>, then rename over <c>{path}</c>. A crash
    /// mid-rename leaves either the old file or the new file — never a
    /// half-written one. On failure, the tmp file is cleaned up and the
    /// exception rethrown with the original path attached.
    /// <para>
    /// v3.8.5 PATCH L1: switched from eager
    /// <c>JsonSerializer.Serialize(snapshot)</c> +
    /// <c>File.WriteAllText(tmp, json)</c> to streaming
    /// <c>JsonSerializer.Serialize(stream, snapshot, JsonOpts)</c> via
    /// <c>Utf8JsonWriter</c> on the file stream. The eager path
    /// allocates the entire JSON envelope on the LOH as a UTF-16
    /// string before any byte hits disk; a 50MB bundle would peak at
    /// ≥100MB working-set. The streaming path writes incrementally and
    /// discards per-iteration buffers as the writer advances, capping
    /// peak memory at the per-write chunk size (default 4KB). Same
    /// atomic-write behavior (tmp + rename), same JSON shape (pretty +
    /// UTF-8 BOM).
    /// </para>
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
            // v3.8.5 PATCH L1: streaming serialization. Utf8JsonWriter
            // writes directly into the FileStream in 4KB-ish chunks,
            // bounded memory regardless of bundle size.
            using (var fs = new FileStream(
                tmp,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: false))
            {
                // UTF-8 BOM matches the prior eager path so existing
                // tools that detect the bundle via BOM keep working.
                fs.Write([0xEF, 0xBB, 0xBF], 0, 3);
                JsonSerializer.Serialize(fs, snapshot, JsonOpts);
            }
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

    // v3.10.0 MINOR T3 (H4): oversized-file load rejection.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Trace session bundle exceeds size cap ({Actual} > {Cap} bytes), treating as corrupt: {Path}")]
    private static partial void LogOversized(ILogger logger, string path, long actual, int cap);

    [LoggerMessage(EventId = 9001, Level = LogLevel.Error, Message = "TraceSessionLibrary save to {Path} failed")]
    private static partial void LogSaveFailed(ILogger logger, Exception ex, string path);
}