using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace PeakCan.Host.App.Services.Ui;

public sealed record LayoutStateDto(
    [property: JsonPropertyName("rightPanelWidth")] double RightPanelWidth,
    [property: JsonPropertyName("selectedMainTab")] int SelectedMainTabIndex,
    [property: JsonPropertyName("selectedRightTab")] int SelectedRightTabIndex);

/// <summary>P2-6: AppShell 内布局持久化（右栏宽 / 主右 tab 选中项）到
/// <c>%APPDATA%/PeakCan.Host/layout.json</c>。镜像 <see cref="WindowStateStore"/>
/// 文件契约：schema 信封、原子 tmp+rename、损坏/超大容错。</summary>
public sealed partial class LayoutStateStore
{
    private const string CurrentSchema = "layout/v1";

    public const long MaxLoadFileBytes = 1 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly ILogger<LayoutStateStore> _logger;
    private LayoutStateDto? _state;

    public LayoutStateStore(ILogger<LayoutStateStore> logger)
        : this(logger, null) { }

    public LayoutStateStore(ILogger<LayoutStateStore> logger, string? overridePath)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _path = overridePath ?? DefaultPath();
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            try { Directory.CreateDirectory(dir); } catch { /* best effort */ }
        }
    }

    public LayoutStateDto? Get() => _state;

    public void Set(LayoutStateDto state)
    {
        _state = state;
        Persist();
    }

    public Task LoadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _state = null;
        if (!File.Exists(_path)) return Task.CompletedTask;
        var info = new FileInfo(_path);
        if (info.Length > MaxLoadFileBytes)
        {
            LogOversized(_logger, _path, info.Length, MaxLoadFileBytes);
            return Task.CompletedTask;
        }
        try
        {
            var json = File.ReadAllText(_path);
            _state = JsonSerializer.Deserialize<Envelope>(json, JsonOpts)?.Layout;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            LogCorrupt(_logger, _path, ex);
        }
        return Task.CompletedTask;
    }

    private void Persist()
    {
        var dto = new Envelope { Layout = _state };
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
        }
    }

    private static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PeakCan.Host", "layout.json");

    [LoggerMessage(Level = LogLevel.Error, Message = "Layout-state file corrupt or unreadable: {Path}")]
    private static partial void LogCorrupt(ILogger logger, string path, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Layout-state file exceeds size cap ({Actual} > {Cap} bytes), treating as empty: {Path}")]
    private static partial void LogOversized(ILogger logger, string path, long actual, long cap);

    [LoggerMessage(Level = LogLevel.Error, Message = "LayoutStateStore save to {Path} failed")]
    private static partial void LogSaveFailed(ILogger logger, Exception ex, string path);

    public sealed class Envelope
    {
        [JsonPropertyName("version")] public string Version { get; set; } = CurrentSchema;
        [JsonPropertyName("layout")] public LayoutStateDto? Layout { get; set; }
    }
}
