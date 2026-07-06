using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace PeakCan.Host.App.Services.Trace;

/// <summary>
/// v3.6.0 MINOR T3: one entry in the File ▸ Open Recent menu. The
/// <see cref="Path"/> is what gets handed to
/// <see cref="ViewModels.TraceViewerViewModel.OpenSessionAsync"/>;
/// <see cref="Label"/> is what the menu shows (just the filename);
/// <see cref="SavedAt"/> is recorded when <see cref="RecentSessionsService.Add"/>
/// runs and surfaces in the menu tooltip via UI plumbing.
/// <para>
/// <b>v3.7.0 MINOR Chunk 2:</b> <see cref="ViewType"/> discriminates
/// which surface produced the entry. Empty string is the legacy-trace
/// value preserved from v3.6.0–v3.6.4 entries (back-compat: those
/// predate the field and never carried a value; they're treated as
/// trace entries for filter purposes). <c>"trace"</c> for Trace
/// Viewer saves (AppShell menu). <c>"replay"</c> for Replay tab
/// saves (ReplayView's Recent submenu). Future surface additions
/// (Signal tab?) pick their own string.
/// </para>
/// </summary>
public sealed record RecentSessionDto(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("savedAt")] DateTimeOffset SavedAt,
    [property: JsonPropertyName("viewType")] string ViewType = "");

/// <summary>
/// v3.6.0 MINOR T3: most-recently-used (MRU) list of opened/saved
/// Trace Viewer session bundles. Capped at
/// <see cref="MaxEntries"/> entries; mutations persist atomically to
/// <c>%APPDATA%/PeakCan.Host/recent-sessions.json</c>; raises
/// <see cref="PropertyChanged"/> for the bound
/// <c>RecentSessionEntries</c> collection in AppShellViewModel.
/// <para>
/// <b>Fail-safe contract:</b> corrupt files load as an empty list
/// (logged at Error). Missing files are silently treated as empty.
/// Mutations never throw — the worst case is a silent log warning if
/// the disk write fails.
/// </para>
/// <para>
/// File format mirrors <see cref="AutoSavePrefsStore"/>:
/// <c>{ version, recent: [...] }</c>. Adding fields is non-breaking
/// (deserializer ignores unknown keys).
/// </para>
/// </summary>
public sealed partial class RecentSessionsService : INotifyPropertyChanged
{
    private const string CurrentSchema = "recent-sessions/v1";

    /// <summary>v3.6.0 MINOR: hard cap. Older entries fall off the bottom.</summary>
    public const int MaxEntries = 5;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly string _path;
    private readonly ILogger<RecentSessionsService> _logger;
    private readonly List<RecentSessionDto> _items = new();

    /// <summary>The on-disk JSON file. Defaults to
    /// <c>%APPDATA%/PeakCan.Host/recent-sessions.json</c>; tests inject
    /// a per-test temp path.</summary>
    public string RecentSessionsPath => _path;

    /// <summary>The current MRU list, most-recent first. Updated after
    /// each <see cref="Add"/>/<see cref="Remove"/>/<see cref="Clear"/>
    /// call and after a successful <see cref="LoadAsync"/>.</summary>
    public IReadOnlyList<RecentSessionDto> Recent => _items;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Production ctor: defaults to
    /// <c>%APPDATA%/PeakCan.Host/recent-sessions.json</c>.</summary>
    public RecentSessionsService(ILogger<RecentSessionsService> logger)
        : this(logger, DefaultPath()) { }

    /// <summary>Test ctor with explicit path.</summary>
    public RecentSessionsService(ILogger<RecentSessionsService> logger, string? overridePath)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _path = overridePath ?? throw new ArgumentNullException(nameof(overridePath));
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            try { Directory.CreateDirectory(dir); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Push <paramref name="path"/> to the top of the MRU with the default
    /// <c>"trace"</c> viewType. Preserved for source-compatibility with
    /// the v3.6.0–v3.6.4 callers that pre-date the viewType discriminator.
    /// </summary>
    public void Add(string path) => Add(path, viewType: "trace");

    /// <summary>
    /// Push <paramref name="path"/> to the top of the MRU tagged with
    /// <paramref name="viewType"/>. If the path is already present
    /// (case-insensitive exact match), remove the older entry first so
    /// we don't double-list — re-adding also refreshes <c>SavedAt</c> to
    /// <c>DateTimeOffset.UtcNow</c>, matching standard MRU semantics (the
    /// entry moves to the top of the list and its timestamp is the most
    /// recent use). Caps the result at <see cref="MaxEntries"/>; older
    /// entries fall off the bottom. Persists atomically (tmp + rename,
    /// UTF-8 BOM). Raises <see cref="PropertyChanged"/> on <c>Recent</c>.
    /// <para>
    /// v3.7.0 MINOR Chunk 2: <paramref name="viewType"/> is the surface
    /// discriminator (<c>"trace"</c> for AppShell, <c>"replay"</c> for
    /// the Replay tab). Filter at the consumer (AppShell filter narrows
    /// to trace/replay entries; Replay VM filters to replay entries) —
    /// the service itself is the unfiltered MRU list.
    /// </para>
    /// </summary>
    public void Add(string path, string viewType)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(viewType);
        // Case-insensitive exact-path match — Windows file paths are
        // case-insensitive at the OS level, so "C:\foo.tmtrace" and
        // "c:\FOO.tmtrace" should not both appear in the list.
        _items.RemoveAll(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
        _items.Insert(0, new RecentSessionDto(
            Path: path,
            Label: Path.GetFileName(path),
            SavedAt: DateTimeOffset.UtcNow,
            ViewType: viewType));
        if (_items.Count > MaxEntries)
        {
            _items.RemoveRange(MaxEntries, _items.Count - MaxEntries);
        }
        Persist();
        Raise();
    }

    /// <summary>
    /// Drop <paramref name="path"/> from the MRU. Case-insensitive exact
    /// match. No-op if the path is not present. Persists and raises.
    /// </summary>
    public void Remove(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var removed = _items.RemoveAll(e =>
            string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return;
        Persist();
        Raise();
    }

    /// <summary>
    /// Empty the entire MRU list (any <see cref="RecentSessionDto.ViewType"/>)
    /// and delete the backing file (best effort). Preserved for the v3.6.0
    /// back-compat caller; new callers should use <see cref="Clear(string)"/>
    /// so a Trace entry's <c>Clear</c> doesn't accidentally wipe the
    /// user's Replay entries (and vice versa).
    /// </summary>
    public void Clear() => Clear(viewType: null);

    /// <summary>
    /// Drop every entry whose <see cref="RecentSessionDto.ViewType"/>
    /// matches <paramref name="viewType"/>. <c>null</c> clears ALL entries
    /// (any viewType) plus the backing file — matches the v3.6.0
    /// <c>Clear()</c> behavior. <c>""</c> clears only legacy-trace
    /// entries (predates the field, ViewType default value).
    /// </summary>
    public void Clear(string? viewType)
    {
        if (viewType is null)
        {
            _items.Clear();
            try
            {
                if (File.Exists(_path)) File.Delete(_path);
            }
            catch (Exception ex)
            {
                LogDeleteFailed(_logger, ex, _path);
            }
            Raise();
            return;
        }
        var removed = _items.RemoveAll(e =>
            string.Equals(e.ViewType, viewType, StringComparison.Ordinal));
        if (removed == 0) return;
        if (_items.Count == 0)
        {
            try
            {
                if (File.Exists(_path)) File.Delete(_path);
            }
            catch (Exception ex)
            {
                LogDeleteFailed(_logger, ex, _path);
            }
        }
        else
        {
            Persist();
        }
        Raise();
    }

    /// <summary>
    /// Read the persisted file into <see cref="Recent"/>. Missing or
    /// corrupt files leave the list empty (logged at Error on corrupt).
    /// Explicit method (rather than a load-eagerly ctor) so unit tests
    /// can construct without a file existing and can defer the load
    /// until after the test arranges a fixture.
    /// </summary>
    public Task LoadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _items.Clear();
        if (!File.Exists(_path))
        {
            Raise();
            return Task.CompletedTask;
        }
        try
        {
            var json = File.ReadAllText(_path);
            var dto = JsonSerializer.Deserialize<Envelope>(json, JsonOpts);
            if (dto?.Recent is { Count: > 0 })
            {
                _items.AddRange(dto.Recent);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            LogCorrupt(_logger, _path, ex);
            // _items already cleared above — corrupt-load leaves the
            // list empty rather than throwing, mirroring the
            // TraceSessionLibrary.Load contract.
        }
        Raise();
        return Task.CompletedTask;
    }

    private void Persist()
    {
        var dto = new Envelope
        {
            Schema = CurrentSchema,
            Recent = _items.ToList(),
        };
        var json = JsonSerializer.Serialize(dto, JsonOpts);
        var tmp = _path + ".tmp";
        try
        {
            File.WriteAllText(tmp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            // Atomic on POSIX (rename) and Windows (MoveFileEx
            // MOVEFILE_REPLACE_EXISTING) — same pattern as
            // TraceSessionLibrary.Save and FileAutoSavePrefsStore.Save.
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            LogSaveFailed(_logger, ex, _path);
        }
    }

    private void Raise() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Recent)));

    private static string DefaultPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "PeakCan.Host", "recent-sessions.json");
    }

    /// <summary>On-disk shape. Internal — public only so
    /// <see cref="JsonSerializer"/> can serialize it.</summary>
    public sealed class Envelope
    {
        [JsonPropertyName("schema")]
        public string Schema { get; set; } = CurrentSchema;

        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("recent")]
        public List<RecentSessionDto> Recent { get; set; } = new();
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Recent-sessions file corrupt or unreadable: {Path}")]
    private static partial void LogCorrupt(ILogger logger, string path, Exception ex);

    [LoggerMessage(EventId = 9201, Level = LogLevel.Error, Message = "RecentSessionsService save to {Path} failed")]
    private static partial void LogSaveFailed(ILogger logger, Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "RecentSessionsService delete of {Path} failed")]
    private static partial void LogDeleteFailed(ILogger logger, Exception ex, string path);
}