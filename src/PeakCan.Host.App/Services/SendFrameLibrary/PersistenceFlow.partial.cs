// SendFrameLibrary/PersistenceFlow.partial.cs — W29 T1 (Flow A)
// Private file-IO lifecycle helpers: EnsureLoaded (cache-init
// sentinel) + LoadUnlocked (JSON deserialize with corrupt-fallback)
// + SaveUnlocked (atomic tmp+rename with IOException cleanup).
// Sister of W22 RecordService/Lifecycle + W27 RecentSessionsService/
// PersistenceOps + W28 DbcService/LoadLifecycle private-helpers-in-
// partial sister-pattern.
//
// 2 [LoggerMessage] declarations (LogCorrupt + LogSaveUnlockedFailed)
// stay on SendFrameLibrary.cs per W18+W22+W23+W25+W26+W27+W28
// sister precedent (CS8795 mitigation).
//
// Cross-partial caller pattern: Mutators partial (Flow B) calls
// EnsureLoaded + LoadUnlocked + SaveUnlocked private helpers via
// partial-class visibility (sister of W22+W23+W24+W25+W26+W27+W28
// cross-partial helper pattern).
//
// W23 STRUCT-FABRICATION LESSON: verify JsonSerializer.Serialize/
// Deserialize 2-arg + File.WriteAllText 3-arg + File.Move 3-arg
// + Interlocked.Increment 1-arg signatures (all verified during
// verbatim re-extraction from HEAD).
//
// W29 T1 verbatim re-extracted via `git show HEAD:src/.../SendFrameLibrary.cs | sed -n '211,264p'`
// per W20 T2 R1 fabrication LESSON (32nd application).

using System.IO;
using System.Text;
using System.Text.Json;

namespace PeakCan.Host.App.Services;

public sealed partial class SendFrameLibrary
{
    private void EnsureLoaded()
    {
        // v1.2.13 PATCH Item 7: warm the cache exactly once per library
        // instance. -1 sentinel means "never loaded"; subsequent calls
        // after any mutator (which sets _cachedCount to current.Count)
        // are no-ops.
        if (_cachedCount >= 0) return;
        Interlocked.Increment(ref CacheMissesForTesting);
        _cachedCount = LoadUnlocked().Count;
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
            File.WriteAllText(tmp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            // v1.2.13 PATCH Item 8: File.Move with overwrite:true is atomic
            // on POSIX (rename) and Windows (MoveFileEx with
            // MOVEFILE_REPLACE_EXISTING). Replaces the v1.2.12
            // Exists→Replace/Move branch which had a small TOCTOU window
            // between the Exists check and the actual move.
            // The counter below is incremented ONLY on this path — if anyone
            // reverts to File.Replace + Exists (or any other save mechanism),
            // this increment is gone and Save_Uses_FileMove_Overwrite_True
            // fails on the counter assertion.
            Interlocked.Increment(ref AtomicSaveMoveCallCount);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            LogSaveUnlockedFailed(_logger, ex, _path);
            throw;
        }
    }
}
