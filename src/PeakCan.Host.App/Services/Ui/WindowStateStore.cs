using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace PeakCan.Host.App.Services.Ui;

/// <summary>
/// Stable identity for a persistable window surface. Shared by
/// <see cref="WindowStateStore"/> (persistence key) and the window
/// registry (D2) — single source of truth, never hand-written strings.
/// </summary>
public enum WindowKey
{
    AppShell,
    TraceViewer,
    Uds,
    EcuScriptEditor,
    MultiFrame,
    Hil,
}

/// <summary>
/// Persisted geometry for one window. <see cref="State"/> is the WPF
/// <c>WindowState</c> name (<c>"Normal"</c> / <c>"Maximized"</c> /
/// <c>"Minimized"</c>) as a string so the store stays free of a WPF
/// dependency; the window layer converts.
/// </summary>
public sealed record WindowStateDto(
    [property: JsonPropertyName("left")] double Left,
    [property: JsonPropertyName("top")] double Top,
    [property: JsonPropertyName("width")] double Width,
    [property: JsonPropertyName("height")] double Height,
    [property: JsonPropertyName("state")] string State);

/// <summary>
/// P0-1: persists per-window geometry to
/// <c>%APPDATA%/PeakCan.Host/window-state.json</c>. Mirrors the
/// <see cref="Trace.RecentSessionsService"/> file contract: schema
/// envelope, atomic tmp+rename save, corrupt/oversized loads treated as
/// empty (never throw), best-effort directory creation.
/// <para>
/// Bounds clamping is intentionally NOT here — it needs WPF
/// <c>SystemParameters</c> (virtual screen), which is the window layer's
/// job at restore time. The store is pure persistence, testable without
/// a UI thread.
/// </para>
/// </summary>
public sealed partial class WindowStateStore
{
    private const string CurrentSchema = "window-state/v1";

    /// <summary>
    /// Oversized-file load rejection ceiling, mirrored from
    /// <see cref="Trace.RecentSessionsService.MaxLoadFileBytes"/>. A stray
    /// binary/logfile dropped at the persisted path must not block the UI
    /// thread at startup. 6 entries × ~80 bytes ≈ 500 bytes legitimate.
    /// </summary>
    public const long MaxLoadFileBytes = 1 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly ILogger<WindowStateStore> _logger;
    private readonly Dictionary<string, WindowStateDto> _windows = new();

    /// <summary>Production ctor: defaults to
    /// <c>%APPDATA%/PeakCan.Host/window-state.json</c>.</summary>
    public WindowStateStore(ILogger<WindowStateStore> logger)
        : this(logger, null) { }

    /// <summary>Test ctor with explicit path.</summary>
    public WindowStateStore(ILogger<WindowStateStore> logger, string? overridePath)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _path = overridePath ?? DefaultPath();
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            try { Directory.CreateDirectory(dir); } catch { /* best effort */ }
        }
    }

    /// <summary>The persisted geometry for <paramref name="key"/>, or
    /// <c>null</c> when no entry exists (first run / cleared).</summary>
    public WindowStateDto? Get(WindowKey key) =>
        _windows.TryGetValue(key.ToString(), out var state) ? state : null;

    /// <summary>Persist <paramref name="state"/> for <paramref name="key"/>
    /// immediately (atomic tmp+rename).</summary>
    public void Set(WindowKey key, WindowStateDto state)
    {
        _windows[key.ToString()] = state;
        Persist();
    }

    /// <summary>Read the persisted file into memory. Missing / corrupt /
    /// oversized files leave the store empty (logged), never throw.
    /// Call once at app startup before windows restore.</summary>
    public Task LoadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _windows.Clear();
        if (!File.Exists(_path))
        {
            return Task.CompletedTask;
        }
        var info = new FileInfo(_path);
        if (info.Length > MaxLoadFileBytes)
        {
            LogOversized(_logger, _path, info.Length, MaxLoadFileBytes);
            return Task.CompletedTask;
        }
        try
        {
            var json = File.ReadAllText(_path);
            var dto = JsonSerializer.Deserialize<Envelope>(json, JsonOpts);
            if (dto?.Windows is { Count: > 0 })
            {
                foreach (var (key, state) in dto.Windows)
                {
                    _windows[key] = state;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            LogCorrupt(_logger, _path, ex);
        }
        return Task.CompletedTask;
    }

    private void Persist()
    {
        var dto = new Envelope { Windows = new Dictionary<string, WindowStateDto>(_windows) };
        var json = JsonSerializer.Serialize(dto, JsonOpts);
        var tmp = _path + ".tmp";
        try
        {
            File.WriteAllText(tmp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            // Atomic on Windows (MoveFileEx MOVEFILE_REPLACE_EXISTING) —
            // same pattern as RecentSessionsService.Persist.
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            LogSaveFailed(_logger, ex, _path);
        }
    }

    private static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PeakCan.Host",
        "window-state.json");

    [LoggerMessage(Level = LogLevel.Error, Message = "Window-state file corrupt or unreadable: {Path}")]
    private static partial void LogCorrupt(ILogger logger, string path, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Window-state file exceeds size cap ({Actual} > {Cap} bytes), treating as empty: {Path}")]
    private static partial void LogOversized(ILogger logger, string path, long actual, long cap);

    [LoggerMessage(EventId = 9202, Level = LogLevel.Error, Message = "WindowStateStore save to {Path} failed")]
    private static partial void LogSaveFailed(ILogger logger, Exception ex, string path);

    /// <summary>On-disk envelope. Extra fields are ignored by the
    /// deserializer, so adding state later is non-breaking.</summary>
    public sealed class Envelope
    {
        [JsonPropertyName("version")] public string Version { get; set; } = CurrentSchema;
        [JsonPropertyName("windows")] public Dictionary<string, WindowStateDto> Windows { get; set; } = new();
    }
}
