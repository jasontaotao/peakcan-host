using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
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
/// <para>
/// v1.2.13 PATCH Item 8: atomic save via <c>File.Move(src, dst, overwrite: true)</c>
/// with UTF-8 BOM. .NET 5+ API is atomic on POSIX (rename) and Windows
/// (MoveFileEx MOVEFILE_REPLACE_EXISTING) — single call, no
/// Exists→Replace/Move TOCTOU branch. On failure, tmp file cleaned and
/// exception rethrown with context.
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
    // v1.2.13 PATCH Item 7: cached frame count. -1 is the unloaded sentinel —
    // an empty library legitimately has count 0, so 0 cannot serve as
    // "needs-load". Lazy-initialized by EnsureLoaded under _gate; kept in
    // sync by every public mutator.
    private int _cachedCount = -1;
    // v1.2.13 PATCH Item 7: counts how many times EnsureLoaded had to fall
    // through to LoadUnlocked (i.e. was a true cache miss). Interlocked for
    // diagnostic visibility under concurrency; the actual cache invariant
    // is preserved by _gate.
    internal int CacheMissesForTesting;

    /// <summary>
    /// v1.2.13 PATCH Item 8 test hook: increments every time
    /// <c>SaveUnlocked</c> uses <c>File.Move</c> with <c>overwrite:true</c>.
    /// Tests assert this counter increments to guard against any regression
    /// that reverts to the old <c>File.Replace</c> + <c>Exists</c> branch
    /// (which would satisfy the behavioral tests but reintroduce the
    /// TOCTOU window). Static so it survives across instances; Interlocked
    /// for atomic increment under the documented concurrency.
    /// </summary>
    internal static int AtomicSaveMoveCallCount;

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
        lock (_gate)
        {
            EnsureLoaded();
            return LoadUnlocked();
        }
    }

    /// <summary>
    /// Persist <paramref name="frames"/> to disk atomically: write to
    /// <c>{path}.tmp</c>, then rename over <c>{path}</c>. A crash mid-rename
    /// leaves either the old file or the new file — never a half-written one.
    /// v1.2.11 PATCH review fix: deletes the orphaned .tmp on failure.
    /// </summary>
    public void Save(IEnumerable<SavedFrame> frames)
    {
        // v1.2.13 PATCH Item 7: caller-supplied list is authoritative for
        // both on-disk state and the cached count.
        var snapshot = frames.ToList();
        lock (_gate)
        {
            SaveUnlocked(snapshot);
            _cachedCount = snapshot.Count;
        }
    }

    /// <summary>
    /// v1.2.12 PATCH Item 13: parameterless save re-writes the current
    /// on-disk library back to disk. Used by tests to trigger the
    /// SaveUnlocked error path without supplying frames.
    /// </summary>
    public void Save()
    {
        lock (_gate)
        {
            EnsureLoaded();
            var current = LoadUnlocked().ToList();
            SaveUnlocked(current);
            _cachedCount = current.Count;
        }
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
            EnsureLoaded();
            var current = LoadUnlocked().ToList();
            current.Add(frame);
            SaveUnlocked(current);
            _cachedCount = current.Count;
            return _cachedCount;
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
            EnsureLoaded();
            var current = LoadUnlocked().ToList();
            int before = current.Count;
            current.RemoveAll(f => f.Name == name);
            if (current.Count == before) return false;
            SaveUnlocked(current);
            _cachedCount = current.Count;
            return true;
        }
    }

    /// <summary>
    /// v1.2.12 PATCH Item 1: number of saved frames. Lock-snapshotted under
    /// <c>_gate</c> so the value is consistent with concurrent Add/Remove.
    /// Used by <c>SendViewModel</c> to surface a count in the post-save
    /// status message.
    /// </summary>
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


    private static string DefaultPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "PeakCan.Host", "send-library.json");
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Send library corrupt or unreadable: {Path}")]
    private static partial void LogCorrupt(ILogger logger, string path, Exception ex);

    [LoggerMessage(EventId = 7001, Level = LogLevel.Error, Message = "SendFrameLibrary save to {Path} failed")]
    private static partial void LogSaveUnlockedFailed(ILogger logger, Exception ex, string path);
}