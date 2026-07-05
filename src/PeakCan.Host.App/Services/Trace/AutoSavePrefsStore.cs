using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace PeakCan.Host.App.Services.Trace;

/// <summary>
/// v3.6.0 MINOR T2: on-disk user preference for the auto-restore prompt.
/// Stored as a tiny JSON envelope in
/// <c>%APPDATA%/PeakCan.Host/auto-save-prefs.json</c>. Read on every
/// app start (small file, no need to cache) and rewritten when the user
/// clicks "No" once to suppress future prompts.
/// <para>
/// Schema is intentionally minimal — a single <see cref="bool"/>
/// flag today. Adding fields later is non-breaking under
/// <see cref="JsonSerializerOptions.PropertyNameCaseInsensitive"/>
/// + skip-unknown behavior (mirrors the <c>.tmtrace</c> envelope).
/// </para>
/// </summary>
public sealed record AutoSavePrefs(bool NeverRestore);

/// <summary>
/// v3.6.0 MINOR T2: load/save the <see cref="AutoSavePrefs"/> singleton.
/// The interface keeps the saver unit-testable: production wires the
/// file-backed <see cref="FileAutoSavePrefsStore"/>; tests inject a
/// memory-backed fake. Corrupt or missing files default to
/// <c>NeverRestore=false</c> (i.e. ask on next startup) — same
/// corrupt-recovery pattern as <see cref="TraceSessionLibrary.Load"/>.
/// </summary>
public interface IAutoSavePrefsStore
{
    /// <summary>Read the persisted prefs. Missing or corrupt file
    /// returns <see cref="AutoSavePrefs.Default"/>.</summary>
    Task<AutoSavePrefs> LoadAsync(CancellationToken ct);

    /// <summary>Atomically write the prefs (tmp + rename).</summary>
    Task SaveAsync(AutoSavePrefs prefs, CancellationToken ct);
}

/// <summary>Default in-memory representation: opt-in to the prompt
/// (NeverRestore=false). Used when the on-disk file is missing or
/// corrupt and by the in-memory test fake.</summary>
public static class AutoSavePrefsDefaults
{
    public static AutoSavePrefs Default { get; } = new(NeverRestore: false);
}

/// <summary>
/// v3.6.0 MINOR T2: file-backed <see cref="IAutoSavePrefsStore"/>.
/// Atomic tmp + rename write, corrupt-recovery-to-default on read.
/// Directory creation is best-effort — if the user's %APPDATA% is
/// read-only the load still works (just returns the default).
/// </summary>
public sealed partial class FileAutoSavePrefsStore : IAutoSavePrefsStore
{
    private const string CurrentSchema = "autosave-prefs/v1";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly string _path;
    private readonly ILogger<FileAutoSavePrefsStore> _logger;

    /// <summary>Production ctor: defaults to
    /// <c>%APPDATA%/PeakCan.Host/auto-save-prefs.json</c>.</summary>
    public FileAutoSavePrefsStore(ILogger<FileAutoSavePrefsStore> logger)
        : this(DefaultPath(), logger) { }

    /// <summary>Test ctor with custom path.</summary>
    public FileAutoSavePrefsStore(string path, ILogger<FileAutoSavePrefsStore> logger)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            try { Directory.CreateDirectory(dir); } catch { /* best effort */ }
        }
    }

    /// <inheritdoc />
    public Task<AutoSavePrefs> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_path))
            return Task.FromResult(AutoSavePrefsDefaults.Default);

        try
        {
            var json = File.ReadAllText(_path);
            var dto = JsonSerializer.Deserialize<Envelope>(json, JsonOpts);
            if (dto is null)
                return Task.FromResult(AutoSavePrefsDefaults.Default);
            return Task.FromResult(new AutoSavePrefs(dto.NeverRestore));
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            LogCorrupt(_logger, _path, ex);
            return Task.FromResult(AutoSavePrefsDefaults.Default);
        }
    }

    /// <inheritdoc />
    public Task SaveAsync(AutoSavePrefs prefs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(prefs);
        var dto = new Envelope
        {
            Schema = CurrentSchema,
            NeverRestore = prefs.NeverRestore,
        };
        var json = JsonSerializer.Serialize(dto, JsonOpts);
        var tmp = _path + ".tmp";
        try
        {
            File.WriteAllText(tmp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            LogSaveFailed(_logger, ex, _path);
            throw;
        }
        return Task.CompletedTask;
    }

    private static string DefaultPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "PeakCan.Host", "auto-save-prefs.json");
    }

    /// <summary>On-disk shape. Internal — public only so
    /// <see cref="JsonSerializer"/> can serialize it.</summary>
    public sealed class Envelope
    {
        [JsonPropertyName("schema")]
        public string Schema { get; set; } = CurrentSchema;

        [JsonPropertyName("neverRestore")]
        public bool NeverRestore { get; set; }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Auto-save prefs corrupt or unreadable: {Path}")]
    private static partial void LogCorrupt(ILogger logger, string path, Exception ex);

    [LoggerMessage(EventId = 9101, Level = LogLevel.Error, Message = "FileAutoSavePrefsStore save to {Path} failed")]
    private static partial void LogSaveFailed(ILogger logger, Exception ex, string path);
}