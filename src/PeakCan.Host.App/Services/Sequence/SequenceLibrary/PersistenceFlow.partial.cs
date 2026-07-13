// SequenceLibrary/PersistenceFlow.partial.cs — W33 T1 (Flow A, 41 LoC)
// Private file-IO lifecycle helpers: EnsureLoaded (cache-init sentinel) +
// LoadUnlocked (JSON deserialize with corrupt-fallback) + SaveUnlocked
// (atomic tmp+rename with IOException cleanup). Sister of W22 RecordService/
// Lifecycle + W27 RecentSessionsService/PersistenceOps + W28 DbcService/
// LoadLifecycle + W29 SendFrameLibrary/PersistenceFlow file-IO lifecycle
// sister-pattern. W33 is explicit "Mirror of SendFrameLibrary" per class xmldoc.
//
// Cross-partial caller pattern (W22+W23+W24+W25+W26+W27+W28+W29+W30+W31+W32 sister):
// Mutators partial calls EnsureLoaded + LoadUnlocked + SaveUnlocked via partial-class visibility.
//
// W23 STRUCT-FABRICATION LESSON: JsonSerializer.Serialize 2-arg +
// JsonSerializer.Deserialize 2-arg + File.WriteAllText 3-arg + File.Move 3-arg
// overload + JsonPropertyName attribute signatures verified during verbatim
// re-extraction from HEAD.
//
// W33 T1 verbatim re-extracted via `git show main:src/.../SequenceLibrary.cs | sed -n '190,194p;196,210p;212,232p'`
// per W20 T2 R1 fabrication LESSON (41st application).

using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace PeakCan.Host.App.Services.Sequence;

public sealed partial class SequenceLibrary
{
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
}
