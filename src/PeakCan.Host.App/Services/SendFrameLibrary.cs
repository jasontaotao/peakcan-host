using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace PeakCan.Host.App.Services;

/// <summary>
/// v1.2.11 PATCH Item 5: persists named CAN frames to
/// <c>%APPDATA%\PeakCan.Host\send-library.json</c>. Atomic writes (tmp +
/// rename) so a crash mid-write leaves the previous good copy intact.
/// Missing or corrupt files load as an empty list (logged at Error).
/// </summary>
public sealed partial class SendFrameLibrary
{
    /// <summary>
    /// One named, saved CAN frame. Round-tripped through JSON verbatim;
    /// renaming a field is a breaking schema change (bump <see cref="LibraryFile.Version"/>).
    /// </summary>
    public sealed record SavedFrame(
        string Name,
        uint RawId,
        bool IsExtended,
        bool IsFd,
        bool IsRtr,
        bool BitRateSwitch,
        string DataHex,
        DateTimeOffset SavedAt);

    /// <summary>
    /// On-disk envelope. Version lets future migrations detect old files.
    /// </summary>
    private sealed class LibraryFile
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("frames")]
        public List<SavedFrame> Frames { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly string _path;
    private readonly ILogger<SendFrameLibrary> _logger;

    /// <summary>Production ctor — uses <c>%APPDATA%\PeakCan.Host\send-library.json</c>.</summary>
    public SendFrameLibrary(ILogger<SendFrameLibrary> logger)
        : this(DefaultPath(), logger) { }

    /// <summary>Test ctor with custom path.</summary>
    internal SendFrameLibrary(string path, ILogger<SendFrameLibrary> logger)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    /// <summary>
    /// Read the library from disk. Returns an empty list if the file is
    /// missing or corrupt (corrupt is logged at Error level so the user can
    /// investigate via the log file).
    /// </summary>
    public IReadOnlyList<SavedFrame> Load()
    {
        if (!File.Exists(_path)) return Array.Empty<SavedFrame>();
        try
        {
            var json = File.ReadAllText(_path);
            var file = JsonSerializer.Deserialize<LibraryFile>(json, JsonOpts);
            return file?.Frames ?? new List<SavedFrame>();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            LogCorrupt(_logger, _path, ex);
            return Array.Empty<SavedFrame>();
        }
    }

    /// <summary>
    /// Persist <paramref name="frames"/> to disk atomically: write to
    /// <c>{path}.tmp</c>, then rename over <c>{path}</c>. A crash mid-rename
    /// leaves either the old file or the new file — never a half-written one.
    /// </summary>
    public void Save(IEnumerable<SavedFrame> frames)
    {
        var file = new LibraryFile { Frames = frames.ToList() };
        var json = JsonSerializer.Serialize(file, JsonOpts);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, overwrite: true);
    }

    private static string DefaultPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "PeakCan.Host", "send-library.json");
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Send library corrupt or unreadable: {Path}")]
    private static partial void LogCorrupt(ILogger logger, string path, Exception ex);
}