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

    /// <summary>
    /// v3.8.8 PATCH F2: maximum persisted-file size in bytes that
    /// <see cref="LoadAsync"/> is willing to read+deserialize. A user
    /// who drops a large file (a logfile, a stray binary) at the
    /// persisted path would otherwise block the WPF UI thread for
    /// the full <c>File.ReadAllText</c> +
    /// <c>JsonSerializer.Deserialize</c> duration at app startup (the
    /// call site is a fire-and-forget
    /// <c>_ = _recentSessions.LoadAsync(...)</c> in
    /// <c>AppShellViewModel</c> ctor). 1 MB is far above any legitimate
    /// recent-sessions payload (5 entries × ~200 bytes ≈ 1 KB) and
    /// gives 1000x headroom for future growth.
    /// </summary>
    public const int MaxLoadFileBytes = 1 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly string _path;
    private readonly ILogger<RecentSessionsService> _logger;
    private readonly List<RecentSessionDto> _items = new();

    /// <summary>The current MRU list, most-recent first. Updated after
    /// each <see cref="Add"/>/<see cref="Clear"/>
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Recent-sessions file corrupt or unreadable: {Path}")]
    private static partial void LogCorrupt(ILogger logger, string path, Exception ex);

    // v3.8.8 PATCH F2: oversized-file load rejection.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Recent-sessions file exceeds size cap ({Actual} > {Cap} bytes), treating as corrupt: {Path}")]
    private static partial void LogOversized(ILogger logger, string path, long actual, int cap);

    [LoggerMessage(EventId = 9201, Level = LogLevel.Error, Message = "RecentSessionsService save to {Path} failed")]
    private static partial void LogSaveFailed(ILogger logger, Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "RecentSessionsService delete of {Path} failed")]
    private static partial void LogDeleteFailed(ILogger logger, Exception ex, string path);
}