using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Microsoft.Extensions.Logging;
using PeakCan.Host.App.Models;

namespace PeakCan.Host.App.Services.Sequence;

/// <summary>
/// v2.1.2 PATCH: persists named multi-frame sequences to
/// <c>%APPDATA%\PeakCan.Host\sequences.json</c>. Mirror of
/// <see cref="SendFrameLibrary"/>: atomic writes (tmp + rename),
/// lock-based concurrency, missing/corrupt → empty list.
///
/// <para>
/// Schema is a JSON envelope with <c>version</c> + <c>sequences</c>
/// list. Each sequence holds a name, mode (concurrent/sequential),
/// delay, iteration count, and a list of rows. Row schema mirrors
/// <see cref="MultiFrameSequenceRow"/> — Raw + DBC rows both
/// round-trip. Renaming a field is a breaking change (bump Version).
/// </para>
/// </summary>
public sealed partial class SequenceLibrary
{
    /// <summary>Send mode for a saved sequence.</summary>
    public enum Mode
    {
        Concurrent = 0,
        Sequential = 1,
    }

    /// <summary>One named, saved sequence. Round-tripped through JSON verbatim.</summary>
    public sealed record SavedSequence(
        string Name,
        Mode Mode,
        int DelayMs,
        int Iterations,
        List<SavedRow> Rows,
        DateTimeOffset SavedAt);

    /// <summary>
    /// Per-row serialization record. Holds a kind tag + the relevant
    /// fields for that kind. Round-trips both Raw and DBC rows.
    /// </summary>
    public sealed class SavedRow
    {
        public RowKind Kind { get; set; } = RowKind.Raw;
        // Raw fields
        public ushort Id { get; set; }
        public string DataHex { get; set; } = "";
        public bool IsExtended { get; set; }
        public bool IsFd { get; set; }
        public bool IsRtr { get; set; }
        public bool IsBitRateSwitch { get; set; }
        public bool IsErrorStateIndicator { get; set; }
        // DBC fields
        public string DbcMessageName { get; set; } = "";
        public List<SavedSignalValue> DbcSignalValues { get; set; } = new();
    }

    public enum RowKind { Raw = 0, Dbc = 1 }

    /// <summary>Per-signal (name, value) pair for DBC rows.</summary>
    public sealed class SavedSignalValue
    {
        public string Name { get; set; } = "";
        public double? Value { get; set; }
    }

    /// <summary>On-disk envelope. Version lets future migrations detect old files.</summary>
    private sealed class LibraryFile
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("sequences")]
        public List<SavedSequence> Sequences { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly string _path;
    private readonly ILogger<SequenceLibrary> _logger;
    // Mirror SendFrameLibrary: lock around Load+Save so concurrent
    // Add/Remove calls don't drop each other's changes.
    private readonly object _gate = new();
    // -1 sentinel = "never loaded"; 0+ = cached count.
    private int _cachedCount = -1;

    /// <summary>Production ctor — uses <c>%APPDATA%\PeakCan.Host\sequences.json</c>.</summary>
    public SequenceLibrary(ILogger<SequenceLibrary> logger)
        : this(DefaultPath(), logger) { }

    /// <summary>Test ctor with custom path.</summary>
    internal SequenceLibrary(string path, ILogger<SequenceLibrary> logger)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    /// <summary>
    /// Read the library from disk. Returns an empty list if the file is
    /// missing or corrupt (corrupt is logged at Error level so the user
    /// can investigate via the log file).
    /// </summary>
    public IReadOnlyList<SavedSequence> Load()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return LoadUnlocked();
        }
    }

    /// <summary>
    /// Persist the entire library atomically: write to
    /// <c>{path}.tmp</c>, then rename over <c>{path}</c>. A crash
    /// mid-rename leaves either the old file or the new file — never
    /// a half-written one.
    /// </summary>
    public void Save(IEnumerable<SavedSequence> sequences)
    {
        var snapshot = sequences.ToList();
        lock (_gate)
        {
            SaveUnlocked(snapshot);
            _cachedCount = snapshot.Count;
        }
    }

    /// <summary>
    /// Atomic Add. Loads the current list, appends
    /// <paramref name="sequence"/>, saves — all under the gate so two
    /// callers don't drop each other's changes. Returns the new count.
    /// If a sequence with the same name already exists, it's replaced
    /// (last-wins, mirrors <c>DidDatabase.AddRange</c>).
    /// </summary>
    public int Add(SavedSequence sequence)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var current = LoadUnlocked().ToList();
            current.RemoveAll(s => s.Name == sequence.Name);
            current.Add(sequence);
            SaveUnlocked(current);
            _cachedCount = current.Count;
            return _cachedCount;
        }
    }

    /// <summary>Atomic Remove-by-Name. Returns true if a sequence was removed.</summary>
    public bool Remove(string name)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var current = LoadUnlocked().ToList();
            int before = current.Count;
            current.RemoveAll(s => s.Name == name);
            if (current.Count == before) return false;
            SaveUnlocked(current);
            _cachedCount = current.Count;
            return true;
        }
    }

    /// <summary>Number of saved sequences.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                EnsureLoaded();
                return _cachedCount;
            }
        }
    }

    private void EnsureLoaded()
    {
        if (_cachedCount >= 0) return;
        _cachedCount = LoadUnlocked().Count;
    }

    private IReadOnlyList<SavedSequence> LoadUnlocked()
    {
        if (!File.Exists(_path)) return Array.Empty<SavedSequence>();
        try
        {
            var json = File.ReadAllText(_path);
            var file = JsonSerializer.Deserialize<LibraryFile>(json, JsonOpts);
            return file?.Sequences ?? new List<SavedSequence>();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            LogCorrupt(_logger, _path, ex);
            return Array.Empty<SavedSequence>();
        }
    }

    private void SaveUnlocked(IEnumerable<SavedSequence> sequences)
    {
        var file = new LibraryFile { Sequences = sequences.ToList() };
        var json = JsonSerializer.Serialize(file, JsonOpts);
        var tmp = _path + ".tmp";
        try
        {
            File.WriteAllText(tmp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            // Atomic on POSIX (rename) and Windows (MoveFileEx
            // MOVEFILE_REPLACE_EXISTING). Mirrors SendFrameLibrary
            // v1.2.13 PATCH Item 8 — no TOCTOU window between Exists
            // check and rename.
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            LogSaveFailed(_logger, ex, _path);
            throw;
        }
    }

    private static string DefaultPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "PeakCan.Host", "sequences.json");
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Sequence library corrupt or unreadable: {Path}")]
    private static partial void LogCorrupt(ILogger logger, string path, Exception ex);

    [LoggerMessage(EventId = 8001, Level = LogLevel.Error, Message = "SequenceLibrary save to {Path} failed")]
    private static partial void LogSaveFailed(ILogger logger, Exception ex, string path);
}