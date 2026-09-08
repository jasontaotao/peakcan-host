using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace PeakCan.Host.App.Services.HilPanel;

public sealed record HilPanelStateDto(
    [property: JsonPropertyName("selectedMode")] string SelectedMode,
    [property: JsonPropertyName("dbcPath")] string DbcPath,
    [property: JsonPropertyName("suitePath")] string SuitePath,
    [property: JsonPropertyName("tracePath")] string TracePath,
    [property: JsonPropertyName("ecuScriptPath")] string EcuScriptPath,
    [property: JsonPropertyName("matrixPath")] string MatrixPath,
    [property: JsonPropertyName("caseLogDirectory")] string CaseLogDirectory,
    [property: JsonPropertyName("enableFaultInjection")] bool EnableFaultInjection,
    [property: JsonPropertyName("captureCaseLogs")] bool CaptureCaseLogs,
    [property: JsonPropertyName("enableAnalyze")] bool EnableAnalyze,
    [property: JsonPropertyName("selectedCaseIds")] IReadOnlyList<string> SelectedCaseIds);

/// <summary>HIL panel state persistence: schema envelope, atomic save, corrupt/oversize tolerance.</summary>
public sealed partial class HilPanelStateStore
{
    private const string CurrentSchema = "hil-panel/v1";
    public const long MaxLoadFileBytes = 1 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly string _path;
    private readonly ILogger<HilPanelStateStore> _logger;
    private HilPanelStateDto? _state;

    public HilPanelStateStore(ILogger<HilPanelStateStore> logger, string? overridePath = null)
    {
        _logger = logger;
        _path = overridePath ?? DefaultPath();
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            try { Directory.CreateDirectory(dir); } catch { }
        }
    }

    public HilPanelStateDto? Get() => _state;

    public void Set(HilPanelStateDto state)
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
            _state = JsonSerializer.Deserialize<Envelope>(json, JsonOpts)?.Panel;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            LogCorrupt(_logger, _path, ex);
        }
        return Task.CompletedTask;
    }

    private void Persist()
    {
        var json = JsonSerializer.Serialize(new Envelope { Panel = _state }, JsonOpts);
        var tmp = _path + ".tmp";
        try
        {
            File.WriteAllText(tmp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            try { File.Delete(tmp); } catch { }
            LogSaveFailed(_logger, ex, _path);
        }
    }

    private static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PeakCan.Host", "hil-panel.json");

    [LoggerMessage(Level = LogLevel.Error, Message = "HIL panel-state file corrupt or unreadable: {Path}")]
    private static partial void LogCorrupt(ILogger logger, string path, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "HIL panel-state file exceeds size cap ({Actual} > {Cap} bytes): {Path}")]
    private static partial void LogOversized(ILogger logger, string path, long actual, long cap);

    [LoggerMessage(Level = LogLevel.Error, Message = "HilPanelStateStore save to {Path} failed")]
    private static partial void LogSaveFailed(ILogger logger, Exception ex, string path);

    public sealed class Envelope
    {
        [JsonPropertyName("version")] public string Version { get; set; } = CurrentSchema;
        [JsonPropertyName("panel")] public HilPanelStateDto? Panel { get; set; }
    }
}
