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
/// <para>
/// v1.2.11 PATCH review fix (HIGH): <see cref="Add"/> / <see cref="Remove"/>
/// wrap the read-modify-write in a lock so concurrent calls (e.g.
/// double-clicked buttons) don't drop each other's changes.
/// </para>
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
    // v1.2.11 PATCH review fix (HIGH): lock around Load+Save so concurrent
    // Add/Remove calls (e.g. user double-clicking Save+Delete) don't drop
    // each other's read-modify-write. The lock wraps the file I/O too
    // because Load+Save from multiple threads would otherwise corrupt the
    // tmp+rename atomic-replace pattern.
    private readonly object _gate = new();

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
    /// v1.2.11 PATCH review fix: takes the gate so callers can't race with
    /// Add / Remove / Save.
    /// </summary>
    public IReadOnlyList<SavedFrame> Load()
    {
        lock (_gate) return LoadUnlocked();
    }

    /// <summary>
    /// Persist <paramref name="frames"/> to disk atomically: write to
    /// <c>{path}.tmp</c>, then rename over <c>{path}</c>. A crash mid-rename
    /// leaves either the old file or the new file — never a half-written one.
    /// v1.2.11 PATCH review fix: deletes the orphaned .tmp on failure.
    /// </summary>
    public void Save(IEnumerable<SavedFrame> frames)
    {
        lock (_gate) SaveUnlocked(frames);
    }

    /// <summary>
    /// v1.2.11 PATCH review fix (HIGH): atomic Add. Loads the current list,
    /// appends <paramref name="frame"/>, saves — all under the gate so two
    /// callers don't drop each other's changes. Returns the new frame count.
    /// </summary>
    public int Add(SavedFrame frame)
    {
        lock (_gate)
        {
            var current = LoadUnlocked().ToList();
            current.Add(frame);
            SaveUnlocked(current);
            return current.Count;
        }
    }

    /// <summary>
    /// v1.2.11 PATCH review fix (HIGH): atomic Remove-by-Name. Returns true
    /// if a frame was removed.
    /// </summary>
    public bool Remove(string name)
    {
        lock (_gate)
        {
            var current = LoadUnlocked().ToList();
            int before = current.Count;
            current.RemoveAll(f => f.Name == name);
            if (current.Count == before) return false;
            SaveUnlocked(current);
            return true;
        }
    }

    private IReadOnlyList<SavedFrame> LoadUnlocked()
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

    private void SaveUnlocked(IEnumerable<SavedFrame> frames)
    {
        var file = new LibraryFile { Frames = frames.ToList() };
        var json = JsonSerializer.Serialize(file, JsonOpts);
        var tmp = _path + ".tmp";
        try
        {
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
    }

    private static string DefaultPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "PeakCan.Host", "send-library.json");
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Send library corrupt or unreadable: {Path}")]
    private static partial void LogCorrupt(ILogger logger, string path, Exception ex);
}