using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace PeakCan.Host.App.Services.HilHistory;

public sealed record HilRunHistoryDto(
    [property: JsonPropertyName("completedAtUtc")] DateTime CompletedAtUtc,
    [property: JsonPropertyName("suitePath")] string SuitePath,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("passed")] bool Passed,
    [property: JsonPropertyName("totalCases")] int TotalCases,
    [property: JsonPropertyName("passedCases")] int PassedCases,
    [property: JsonPropertyName("failedCases")] int FailedCases,
    [property: JsonPropertyName("skippedCases")] int SkippedCases,
    [property: JsonPropertyName("elapsedMs")] int ElapsedMs,
    [property: JsonPropertyName("cancelled")] bool Cancelled,
    [property: JsonPropertyName("errorMessage")] string? ErrorMessage,
    [property: JsonPropertyName("reportPath")] string? ReportPath,
    [property: JsonPropertyName("caseLogDirectory")] string? CaseLogDirectory)
{
    public string SuiteName => Path.GetFileName(SuitePath);
    public string ResultText => $"{PassedCases}/{TotalCases} 通过";
}

public sealed partial class HilRunHistoryStore
{
    private const string CurrentSchema = "hil-history/v1";
    public const long MaxLoadFileBytes = 1 * 1024 * 1024;
    public const int MaxRecords = 50;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ILogger<HilRunHistoryStore> _logger;
    private List<HilRunHistoryDto> _history = [];

    public HilRunHistoryStore(ILogger<HilRunHistoryStore> logger, string? overridePath = null)
    {
        _logger = logger;
        _path = overridePath ?? DefaultPath();
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            try { Directory.CreateDirectory(dir); } catch { }
        }
    }

    public IReadOnlyList<HilRunHistoryDto> Get() => _history.AsReadOnly();

    public void Append(HilRunHistoryDto record)
    {
        _history.Add(record);
        if (_history.Count > MaxRecords)
            _history = _history.Skip(_history.Count - MaxRecords).ToList();
        Persist();
    }

    public Task LoadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _history = [];
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
            _history = JsonSerializer.Deserialize<Envelope>(json, JsonOpts)?.History ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            LogCorrupt(_logger, _path, ex);
        }
        return Task.CompletedTask;
    }

    private void Persist()
    {
        var json = JsonSerializer.Serialize(new Envelope { History = _history }, JsonOpts);
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
        "PeakCan.Host", "hil-history.json");

    [LoggerMessage(Level = LogLevel.Error, Message = "HIL history file corrupt or unreadable: {Path}")]
    private static partial void LogCorrupt(ILogger logger, string path, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "HIL history file exceeds size cap ({Actual} > {Cap} bytes): {Path}")]
    private static partial void LogOversized(ILogger logger, string path, long actual, long cap);

    [LoggerMessage(Level = LogLevel.Error, Message = "HilRunHistoryStore save to {Path} failed")]
    private static partial void LogSaveFailed(ILogger logger, Exception ex, string path);

    public sealed class Envelope
    {
        [JsonPropertyName("version")] public string Version { get; set; } = CurrentSchema;
        [JsonPropertyName("history")] public List<HilRunHistoryDto>? History { get; set; }
    }
}
